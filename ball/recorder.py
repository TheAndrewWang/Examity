#!/usr/bin/env python3
"""Smiley Ball recorder - runs on the UNO Q's Linux side.

  recorder.py          Normal mode: button records lectures, HTTP server for the laptop app
  recorder.py --test   Print button events and cycle through the screens (no recording)

The sketch (smiley_ball/smiley_ball.ino) must be running on the microcontroller.
"""
import argparse
import json
import os
import re
import secrets
import select
import shutil
import signal
import socket
import subprocess
import sys
import threading
import time
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import msgpack

HERE = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(HERE, "config.json")
ROUTER_SOCK = "/var/run/arduino-router.sock"

# Faces - must match the Face enum in the sketch.
IDLE, RECORDING, WINK, BUSY, FROWN, FLAT = range(6)

DEFAULTS = {
    "token": "",                 # shared secret; the laptop app sends it as X-Ball-Token
    "port": 8765,
    "mic": "auto",               # ALSA card name, e.g. "GoMic", or "auto" to search for the Samson
    "recordings_dir": os.path.join(HERE, "recordings"),
    "chunk_seconds": 600,        # 10-minute WAV files (well under Fish Audio's 50 MB limit)
    "sample_rate": 16000,
    "low_disk_mb": 500,
}


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


def load_config(path=CONFIG_PATH):
    cfg = dict(DEFAULTS)
    if os.path.exists(path):
        with open(path) as f:
            cfg.update(json.load(f))
    if not cfg["token"]:
        cfg["token"] = secrets.token_urlsafe(18)
        saved = {}
        if os.path.exists(path):
            with open(path) as f:
                saved = json.load(f)
        saved["token"] = cfg["token"]
        with open(path, "w") as f:
            json.dump(saved, f, indent=2)
        log(f"Saved a new token in {path}. Put this token in the laptop app:")
        log(f"   {cfg['token']}")
    os.makedirs(cfg["recordings_dir"], exist_ok=True)
    return cfg


# ---------------------------------------------------------------- bridge link
class Link:
    """Talks to the microcontroller through the arduino-router (same as spotify_am)."""

    def __init__(self):
        self.s = None
        self.unpacker = msgpack.Unpacker()
        self.msgid = 0
        self.ok = False      # did the last call get an answer?
        self.errors_seen = set()

    def _connect(self):
        self.s = socket.socket(socket.AF_UNIX)
        self.s.connect(ROUTER_SOCK)
        self.unpacker = msgpack.Unpacker()

    def call(self, method, *params, timeout=0.5):
        """Call a Bridge function on the MCU. Returns its result; self.ok says if it answered."""
        self.ok = False
        try:
            if self.s is None:
                self._connect()
            self.msgid = (self.msgid + 1) & 0x7FFFFFFF
            my_id = self.msgid
            self.s.sendall(msgpack.packb([0, my_id, method, list(params)]))
            end = time.monotonic() + timeout
            while time.monotonic() < end:
                r, _, _ = select.select([self.s], [], [], max(0, end - time.monotonic()))
                if not r:
                    break
                data = self.s.recv(4096)
                if not data:
                    raise ConnectionError("router closed the connection")
                self.unpacker.feed(data)
                for msg in self.unpacker:
                    if msg[0] == 1 and msg[1] == my_id:
                        if msg[2] is not None:
                            if str(msg[2]) not in self.errors_seen:   # log each error once
                                log("Bridge error:", msg[2], "- is smiley_ball.ino uploaded?")
                                self.errors_seen.add(str(msg[2]))
                            return None
                        self.ok = True
                        self.errors_seen.clear()
                        return msg[3]
            return None
        except (OSError, ConnectionError) as e:
            if self.s is not None:
                log("Bridge connection lost:", e)
            try:
                if self.s:
                    self.s.close()
            finally:
                self.s = None
            return None


# ---------------------------------------------------------------- microphone
def find_mic(cfg):
    """Return the ALSA card id of the Go Mic (or the configured mic), or None."""
    try:
        with open("/proc/asound/cards") as f:
            cards = f.read()
    except OSError:
        return None
    # Lines look like: " 1 [GoMic          ]: USB-Audio - Samson GoMic"
    found = re.findall(r"^\s*\d+\s+\[(\S+)\s*\]:\s*(.*)$", cards, re.M)
    if cfg["mic"] != "auto":
        return cfg["mic"] if any(cid == cfg["mic"] for cid, _ in found) else None
    for cid, desc in found:
        if re.search(r"samson|go\s*mic", cid + " " + desc, re.I):
            return cid
    for cid, desc in found:            # any USB mic will do
        if "USB-Audio" in desc:
            return cid
    return None


def free_mb(path):
    return shutil.disk_usage(path).free // (1024 * 1024)


def part_number(name):
    m = re.search(r"-(\d+)\.wav$", name)
    return int(m.group(1)) if m else 0


# ---------------------------------------------------------------- recorder
class Recorder:
    def __init__(self, cfg):
        self.cfg = cfg
        self.proc = None
        self.dir = None
        self.meta = None
        self.started = 0.0
        self.lock = threading.Lock()

    @property
    def recording(self):
        return self.proc is not None

    def _save_meta(self):
        tmp = os.path.join(self.dir, "session.json.tmp")
        with open(tmp, "w") as f:
            json.dump(self.meta, f, indent=2)
        os.replace(tmp, os.path.join(self.dir, "session.json"))

    def start(self, mic):
        with self.lock:
            sid = datetime.now().strftime("%Y-%m-%d_%H%M%S")
            self.dir = os.path.join(self.cfg["recordings_dir"], sid)
            os.makedirs(self.dir)
            self.started = time.monotonic()
            self.meta = {"id": sid, "started": datetime.now().astimezone().isoformat(),
                         "ended": None, "duration": 0, "bookmarks": [], "finished": False}
            self._save_meta()
            cmd = ["arecord", "-q", "-D", f"plughw:CARD={mic}", "-f", "S16_LE",
                   "-r", str(self.cfg["sample_rate"]), "-c", "1",
                   "--max-file-time", str(self.cfg["chunk_seconds"]),
                   os.path.join(self.dir, "part.wav")]
            self.proc = subprocess.Popen(cmd)
            log("Recording started:", sid)

    def bookmark(self):
        with self.lock:
            if not self.recording:
                return
            sec = round(time.monotonic() - self.started, 1)
            self.meta["bookmarks"].append(sec)
            self._save_meta()
            log(f"Bookmark at {sec:.0f}s")

    def stop(self):
        with self.lock:
            if not self.recording:
                return
            if self.proc.poll() is None:
                self.proc.send_signal(signal.SIGINT)   # lets arecord finish the WAV header
                try:
                    self.proc.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    self.proc.kill()
            self.proc = None
            self.meta["ended"] = datetime.now().astimezone().isoformat()
            self.meta["duration"] = round(time.monotonic() - self.started)
            self.meta["finished"] = True
            self._save_meta()
            log(f"Recording saved: {self.meta['id']} ({self.meta['duration']}s)")

    def check_alive(self):
        """If arecord quit by itself (e.g. the mic was unplugged), save what we have."""
        if self.proc is not None and self.proc.poll() is not None:
            log("arecord stopped unexpectedly - saving what we have")
            self.stop()


# ---------------------------------------------------------------- HTTP server for the laptop
def list_sessions(cfg):
    out = []
    root = cfg["recordings_dir"]
    for sid in sorted(os.listdir(root)):
        path = os.path.join(root, sid, "session.json")
        try:
            with open(path) as f:
                meta = json.load(f)
        except (OSError, ValueError):
            continue
        if not meta.get("finished"):
            continue
        files = sorted((n for n in os.listdir(os.path.join(root, sid)) if n.endswith(".wav")),
                       key=part_number)
        meta["files"] = [{"name": n, "size": os.path.getsize(os.path.join(root, sid, n))}
                         for n in files]
        out.append(meta)
    return out


SID_RE = re.compile(r"^\d{4}-\d{2}-\d{2}_\d{6}$")
FILE_RE = re.compile(r"^part(-\d+)?\.wav$")


def make_handler(cfg, state):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *args):
            pass

        def _auth(self):
            if secrets.compare_digest(self.headers.get("X-Ball-Token", ""), cfg["token"]):
                return True
            self._json(401, {"error": "bad token"})
            return False

        def _json(self, code, obj):
            body = json.dumps(obj).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _route(self):
            """Split the URL into parts; answers 404/401 itself and returns None if not allowed."""
            p = self.path.split("?")[0].strip("/").split("/")
            if p[:2] not in (["api", "recordings"], ["api", "status"]):
                self._json(404, {"error": "not found"})
                return None
            return p if self._auth() else None

        def do_GET(self):
            p = self._route()
            if p is None:
                return
            if p == ["api", "status"]:
                return self._json(200, {"recording": state["recording"], "mic": state["mic"],
                                        "free_mb": free_mb(cfg["recordings_dir"])})
            if len(p) == 2:
                return self._json(200, list_sessions(cfg))
            if len(p) == 4 and SID_RE.match(p[2]) and FILE_RE.match(p[3]):
                path = os.path.join(cfg["recordings_dir"], p[2], p[3])
                if not os.path.isfile(path):
                    return self._json(404, {"error": "no such file"})
                state["last_sync"] = time.monotonic()
                size = os.path.getsize(path)
                self.send_response(200)
                self.send_header("Content-Type", "audio/wav")
                self.send_header("Content-Length", str(size))
                self.end_headers()
                with open(path, "rb") as f:
                    shutil.copyfileobj(f, self.wfile, 256 * 1024)
                return
            self._json(404, {"error": "not found"})

        def do_DELETE(self):
            p = self._route()
            if p is None:
                return
            if len(p) == 3 and SID_RE.match(p[2]):
                d = os.path.join(cfg["recordings_dir"], p[2])
                try:
                    with open(os.path.join(d, "session.json")) as f:
                        finished = json.load(f).get("finished")
                except (OSError, ValueError):
                    return self._json(404, {"error": "no such recording"})
                if not finished:
                    return self._json(409, {"error": "still recording"})
                shutil.rmtree(d)
                log("Laptop collected", p[2], "- deleted from the ball")
                return self._json(200, {"deleted": p[2]})
            self._json(404, {"error": "not found"})

    return Handler


# ---------------------------------------------------------------- main
def test_mode():
    link = Link()
    print("Press the button (short press and 2 s hold). The screen changes every 2 s. Ctrl+C to quit.")
    face, next_face = 0, time.monotonic()
    while True:
        e = link.call("btn_event")
        if e is None:
            print("No answer from the microcontroller - is the sketch running?")
            time.sleep(1)
        elif e:
            print("button:", "short press" if e == 1 else "hold (bookmark)")
        if time.monotonic() >= next_face:
            link.call("set_face", face)
            print("face", face)
            face, next_face = (face + 1) % 6, time.monotonic() + 2
        time.sleep(0.05)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--test", action="store_true", help="test the button and screen")
    ap.add_argument("--config", default=CONFIG_PATH, help="config file (default: config.json here)")
    args = ap.parse_args()
    if args.test:
        return test_mode()

    cfg = load_config(args.config)
    state = {"recording": False, "mic": None, "last_sync": -100.0}
    rec = Recorder(cfg)
    server = ThreadingHTTPServer(("0.0.0.0", cfg["port"]), make_handler(cfg, state))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    log(f"Smiley Ball ready. Laptop app URL: http://{socket.gethostname()}.local:{cfg['port']}")

    def shutdown(*_):
        rec.stop()
        sys.exit(0)
    signal.signal(signal.SIGTERM, shutdown)
    signal.signal(signal.SIGINT, shutdown)

    link = Link()
    shown_face, last_check, low_disk = None, 0.0, False
    while True:
        now = time.monotonic()
        if now - last_check > 5:
            last_check = now
            state["mic"] = find_mic(cfg)
            low_disk = free_mb(cfg["recordings_dir"]) < cfg["low_disk_mb"]
            rec.check_alive()

        event = link.call("btn_event")
        if event == 1:
            if rec.recording:
                rec.stop()
            elif state["mic"] is None:
                log("Can't record: the Go Mic isn't plugged in")
            elif low_disk:
                log("Can't record: the disk is almost full - sync with the laptop app")
            else:
                rec.start(state["mic"])
        elif event == 2:
            rec.bookmark()
        state["recording"] = rec.recording

        if rec.recording:
            face = RECORDING
        elif state["mic"] is None:
            face = FROWN
        elif low_disk:
            face = FLAT
        elif now - state["last_sync"] < 3:
            face = BUSY
        else:
            face = IDLE
        if face != shown_face:
            link.call("set_face", face)
            shown_face = face if link.ok else None   # retry next time if the MCU didn't answer
        time.sleep(0.05 if link.ok else 1.0)          # back off while the sketch isn't running


if __name__ == "__main__":
    main()
