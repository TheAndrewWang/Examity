# Smiley Ball 🙂

A little ball with a smiley face that records your lectures. A web app on your laptop turns each recording into notes, keeps them in a folder Claude can read, and plays a 2–3 minute spoken study talk before you start homework.

```
In class                                          At home (laptop)
────────                                          ────────────────
press button ─► UNO Q records with the Go Mic ─► Smiley Ball web app downloads the recording
LCD shows a smiley + status                        → Fish Audio: transcript
hold button 2 s = ⭐ bookmark                       → Claude: notes + study-talk script
                                                   → Fish Audio: spoken study talk (MP3)
                                                   → Lectures/ folder + web page: notes, player, "Ask Smiley"
```

## What's in this folder

| Path | What it is |
|---|---|
| `ball/smiley_ball/smiley_ball.ino` | Microcontroller sketch: button on D2 + smiley and status on the 16x2 LCD |
| `ball/smiley_ball/lcd_i2c.h` | Small built-in driver for the I2C LCD (no library to install) |
| `ball/recorder.py` | Linux side of the UNO Q: records with the Go Mic and serves recordings to the laptop |
| `ball/smiley-ball.service` | Starts the recorder automatically when the ball is powered on |
| `webapp/SmileyBall.Web/` | C# (ASP.NET Core) web app for your laptop |
| `wiring/wiring.svg` | Wiring diagram |

## Parts

- Arduino UNO Q
- 1 tactile push button (4 legs) + small breadboard
- 16x2 character LCD with an I2C backpack (4 pins: GND, VCC, SDA, SCL)
- 6 jumper wires
- Samson Go Mic and its USB cable
- **USB-C hub with power pass-through (PD)**: the UNO Q has one USB-C port, and it has to carry both power and the mic
- USB power bank (3 A or more) so the ball works in class
- A ball-shaped shell with a window for the LCD, with a smiley drawn on it

## Wiring

See `wiring/wiring.svg`.

| From | To |
|---|---|
| Button leg A | **D2** |
| Button leg B (diagonally opposite) | **GND** |
| LCD **GND** | **GND** |
| LCD **VCC** | **5V** (power header) |
| LCD **SDA** | **SDA** (top header, next to AREF) |
| LCD **SCL** | **SCL** (top header, the very first pin) |
| Go Mic + power bank | USB-C hub → the UNO Q's USB-C port |

- Use two legs of the button that are **diagonally opposite**. Those are always connected through the switch.
- No resistors are needed. The sketch turns on the UNO Q's internal pull-up, so D2 reads LOW while the button is pressed.
- Power the LCD from **5V**. Most 16x2 LCDs are too faint at 3.3 V. The SDA/SCL pins next to AREF are 5 V-tolerant, according to the UNO Q datasheet, so a 5 V backpack is safe there.
- **Don't** connect the LCD to A4/A5. Those pins are *not* 5 V-tolerant.
- The code finds the LCD's I2C address by itself (usually 0x27 or 0x3F).

## What the screen shows

| Screen | Meaning |
|---|---|
| ☺ Smiley Ball / Tap to record | Ready |
| ☺ Recording ● / 12:34 ★ x2 | Recording: time so far and number of bookmarks (the dot blinks) |
| 😉 Bookmarked! / at 12:34 | You held the button for 2 s during a recording |
| 😮 Syncing... / Sending to PC | Your laptop is downloading recordings |
| ☹ No microphone / Plug in Go Mic | Go Mic not found |
| 😐 Disk is full / Sync w/ laptop | Almost out of space: sync with the laptop |

## Set up the ball (on the UNO Q)

1. **Upload the sketch** `ball/smiley_ball/smiley_ball.ino`. Use the same method you used for `am_radio`: either App Lab, or
   `arduino-cli compile --upload --fqbn arduino:zephyr:unoq ball/smiley_ball`.
   This replaces the AM radio sketch. To use the radio again, upload `am_radio` again.
2. **Test the button and screen:**
   ```bash
   cd ~/Projects/smiley_ball/ball
   .venv/bin/python recorder.py --test
   ```
   Short presses and holds should print, and the screen should change every 2 seconds.
   If the LCD lights up but shows nothing (or only white boxes), turn the small blue contrast screw on the backpack.
3. **First real run** (plug in the Go Mic first):
   ```bash
   .venv/bin/python recorder.py
   ```
   On the first run it creates `config.json` with a random **token** and prints it. Copy the token: the laptop app needs it.
   Press the button, talk for 20 seconds, then press it again. You'll find a WAV file in `ball/recordings/`.
4. **Start it automatically at power-on:**
   ```bash
   mkdir -p ~/.config/systemd/user
   cp smiley-ball.service ~/.config/systemd/user/
   systemctl --user daemon-reload
   systemctl --user enable --now smiley-ball
   sudo loginctl enable-linger arduino
   journalctl --user -u smiley-ball -f     # watch the log
   ```

Recordings are saved as 10-minute WAV files (16 kHz mono, about 115 MB per hour). The ball deletes them once the laptop has a copy.

## Set up the web app (on your laptop)

You need the [.NET SDK](https://dotnet.microsoft.com/download) (version 8 or newer). Copy `webapp/` to your laptop, then:

```bash
cd webapp/SmileyBall.Web
dotnet user-secrets set "SmileyBall:AnthropicApiKey" "sk-ant-..."
dotnet user-secrets set "SmileyBall:FishApiKey"      "your-fish-audio-key"
dotnet user-secrets set "SmileyBall:BallToken"       "token-printed-by-recorder.py"
dotnet run
```

Open **http://localhost:5080**.

In `appsettings.json`:
- `FishVoiceId`: pick a voice on fish.audio, copy its ID from the voice page, and paste it here. If you leave it empty, Fish Audio's default voice is used.
- `Courses`: optional list, for example `["Calc II", "Chem 101"]`. Claude files each lecture under one of these.
- `StudentName`: optional. Smiley will greet you by name.
- `BallUrl`: `http://AtlistGoon.local:8765` (the UNO Q's name on your Wi-Fi). If `.local` names don't work on your laptop, use the ball's IP address.
- `LecturesPath`: where the lecture folder lives (default: `Lectures` next to the app).

## Day to day

- **In class:** press the button when the lecture starts, and press it again at the end. Hold it for 2 seconds whenever something sounds important.
- **At home:** open the app. When the laptop and ball are on the same Wi-Fi, recordings come over by themselves (or click **Sync**). After a few minutes the lecture shows **ready**.
- **Before homework:** press **▶ Play my 2–3 min study talk** at the top. It covers what the last lecture was about, how it connects to the one before it, a mini-lesson on the most important idea, and what to focus on for homework.
- **Questions:** use **Ask Smiley** on the right. Choose one lecture to include its full transcript, or ask about all of them.
- **No ball handy?** **Upload** takes any audio file (for example, a phone recording).

## Your Claude folder

Everything is saved as plain files in `Lectures/`:

```
Lectures/
  CLAUDE.md            how this folder is organized (Claude reads it automatically)
  INDEX.md             every lecture by course, newest first, with an overview
  2026-11-07_101500/
    notes.md           key ideas, terms, examples, homework/exam hints, ⭐ bookmarks
    transcript.md      full transcript with [mm:ss] time marks
    briefing.md        the study-talk script
    briefing.mp3       the study talk
    audio/             original recording
```

You can also run `claude` inside `Lectures/` and ask things like "what did we cover this week in Chem?" or "make me a practice quiz from the last two lectures".

## Troubleshooting

- **The app says "Ball: not found".** School Wi-Fi often blocks devices from talking to each other. Sync at home, or put both on your phone's hotspot. Recording works without any Wi-Fi.
- **LCD is blank.** Check the four wires (SDA and SCL swapped is the usual mistake), then turn the blue contrast screw on the backpack. The screen comes alive within 3 seconds of being connected; the board doesn't need a restart.
- **"No microphone" on the screen.** The Go Mic isn't detected. Check the hub, then run `arecord -l`. If its name isn't auto-detected, set `"mic": "<card name>"` in `ball/config.json`.
- **A lecture shows "needs attention".** Open it to read the error (usually an API key or credit problem), fix that, and click **Try again**. Steps that already finished aren't repeated or paid for again.
- **Costs.** Each lecture uses Fish Audio speech-to-text for its length, Claude for the notes and script, and Fish Audio text-to-speech for about 400 words.
