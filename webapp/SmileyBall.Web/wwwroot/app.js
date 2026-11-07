// Smiley Ball web app - plain JavaScript, no build step.

const $ = (sel, root = document) => root.querySelector(sel);
const view = $("#view");
const STATUS_TEXT = {
  downloading: "downloading", new: "waiting", transcribing: "transcribing",
  "writing-notes": "writing notes", "writing-briefing": "writing study talk",
  "recording-voice": "recording voice", ready: "ready", error: "needs attention",
};
const SMILEY_SVG = `<svg class="smiley big-smiley" viewBox="0 0 100 100" aria-hidden="true">
  <circle cx="50" cy="50" r="46" class="face"/><circle cx="35" cy="40" r="6" class="ink"/>
  <circle cx="65" cy="40" r="6" class="ink"/><path d="M28 60 Q50 80 72 60" class="mouth"/></svg>`;

let lectures = [];
let lastStatusKey = "";
let chatHistory = [];

// ---------------------------------------------------------------- helpers
function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
function md(text) {
  if (window.marked && window.DOMPurify) return DOMPurify.sanitize(marked.parse(text ?? ""));
  return `<pre>${esc(text)}</pre>`;   // CDN unavailable: show plain text
}
function fmtDate(iso, long = false) {
  const d = new Date(iso);
  return d.toLocaleDateString(undefined, long
    ? { weekday: "long", month: "long", day: "numeric", year: "numeric" }
    : { weekday: "short", month: "short", day: "numeric" });
}
function mins(sec) { return `${Math.max(1, Math.round((sec || 0) / 60))} min`; }
function badge(status) {
  const cls = status === "ready" ? "ready" : status === "error" ? "error" : "busy";
  return `<span class="badge ${cls}">${esc(STATUS_TEXT[status] ?? status)}</span>`;
}
async function api(path, opts = {}) {
  const res = await fetch(`/api${path}`, opts);
  if (!res.ok) throw new Error(await res.text() || res.statusText);
  if (res.status === 204) return null;
  const type = res.headers.get("content-type") || "";
  return type.includes("json") ? res.json() : res.text();
}
const post = (path, body) => api(path, {
  method: "POST",
  headers: body ? { "Content-Type": "application/json" } : {},
  body: body ? JSON.stringify(body) : undefined,
});

// ---------------------------------------------------------------- home
async function renderHome() {
  const [study, list] = await Promise.all([api("/study"), api("/lectures")]);
  lectures = list;
  updateScope();

  const studyHtml = study ? `
    <section class="card study" id="study-card">
      <div class="row">
        ${SMILEY_SVG}
        <div style="flex:1;min-width:200px">
          <div class="eyebrow">Study now · last lecture</div>
          <h1>${esc(study.lecture.title)}</h1>
          <div class="muted">${esc(study.lecture.course)} · ${fmtDate(study.lecture.date, true)}</div>
        </div>
        <button class="btn primary" id="play-study">▶ Play my 2–3 min study talk</button>
      </div>
      <p>${esc(study.lecture.overview)}</p>
      <audio id="study-audio" controls preload="none" src="/api/lectures/${study.lecture.id}/briefing.mp3"></audio>
      <details><summary>Read the script</summary><div class="script">${esc(study.briefing)}</div></details>
      <div class="actions"><a class="btn" href="#/lecture/${study.lecture.id}">Open full notes</a></div>
    </section>` : `
    <section class="card empty">
      ${SMILEY_SVG}
      <h1>No study talk yet</h1>
      <p class="muted">Press the ball's button at the start of class and again at the end.<br>
      When you're home, the recording shows up here with notes and a 2–3 minute study talk.</p>
    </section>`;

  const groups = {};
  for (const l of list) (groups[l.course] ??= []).push(l);
  const courses = Object.keys(groups).sort((a, b) => (a === "Unsorted") - (b === "Unsorted") || a.localeCompare(b));
  const listHtml = list.length ? courses.map(c => `
      <div class="course">
        <h3>${esc(c)}</h3>
        ${groups[c].map(l => `
          <a class="lec" href="#/lecture/${l.id}">
            <div class="head"><span class="title">${esc(l.title)}</span>${badge(l.status)}</div>
            <p>${fmtDate(l.date)} · ${mins(l.durationSec)}${l.overview ? " · " + esc(l.overview) : ""}</p>
          </a>`).join("")}
      </div>`).join("") : `<p class="muted">Your lectures will be listed here by course.</p>`;

  view.innerHTML = `${studyHtml}<section class="card"><h2>Lectures</h2>${listHtml}</section>`;

  const audio = $("#study-audio");
  if (audio) {
    const card = $("#study-card");
    $("#play-study").onclick = () => (audio.paused ? audio.play() : audio.pause());
    audio.onplay = () => { card.classList.add("playing"); $("#play-study").textContent = "❚❚ Pause"; };
    audio.onpause = audio.onended = () => { card.classList.remove("playing"); $("#play-study").textContent = "▶ Play my 2–3 min study talk"; };
  }
}

// ---------------------------------------------------------------- one lecture
async function renderLecture(id) {
  const data = await api(`/lectures/${encodeURIComponent(id)}`);
  const l = data.lecture;
  if (!lectures.length) { lectures = await api("/lectures"); updateScope(); }
  $("#ask-scope").value = l.id;

  const recordings = (l.audioFiles || []).map((f, i) => `
      <div class="small muted">Recording part ${i + 1}</div>
      <audio controls preload="none" src="/api/lectures/${l.id}/audio/${encodeURIComponent(f)}"></audio>`).join("");

  view.innerHTML = `
    <section class="card lecture">
      <a href="#/" class="small muted">← All lectures</a>
      <h1>${esc(l.title)}</h1>
      <div class="meta">${esc(l.course)} · ${fmtDate(l.date, true)} · ${mins(l.durationSec)} ${badge(l.status)}
        ${l.bookmarks?.length ? `· ⭐ ${l.bookmarks.length} bookmark${l.bookmarks.length > 1 ? "s" : ""}` : ""}</div>
      ${l.error ? `<p class="error-text">⚠️ ${esc(l.error)}</p>` : ""}
      <div class="edit-row">
        <input type="text" id="edit-title" value="${esc(l.title)}" aria-label="Title">
        <input type="text" id="edit-course" value="${esc(l.course)}" aria-label="Course" list="course-list">
        <button class="btn" id="save-edit">Save</button>
      </div>
      <datalist id="course-list">${[...new Set(lectures.map(x => x.course))].map(c => `<option value="${esc(c)}">`).join("")}</datalist>
      <div class="actions">
        ${l.status === "error" ? `<button class="btn primary" id="retry">Try again</button>` : ""}
        <button class="btn" id="regen-talk" title="Writes a new study talk from the same notes">New study talk</button>
        <button class="btn" id="regen-notes" title="Rewrites the notes and talk from the transcript">Rewrite notes</button>
        <button class="btn danger" id="delete">Delete</button>
      </div>
    </section>

    ${l.hasBriefingAudio ? `
    <section class="card study">
      <div class="eyebrow">Study talk</div>
      <audio controls preload="none" src="/api/lectures/${l.id}/briefing.mp3"></audio>
      <details><summary>Read the script</summary><div class="script">${esc(data.briefing)}</div></details>
    </section>` : ""}

    <section class="card md">${data.notes ? md(data.notes) : `<p class="muted">Notes will appear here once the lecture is processed.</p>`}</section>

    ${data.transcript ? `
    <section class="card">
      <details><summary>Full transcript</summary><div class="transcript">${esc(data.transcript)}</div></details>
    </section>` : ""}

    ${recordings ? `<section class="card lecture"><h2>Original recording</h2>${recordings}</section>` : ""}`;

  $("#save-edit").onclick = async () => {
    await api(`/lectures/${l.id}`, {
      method: "PUT", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title: $("#edit-title").value, course: $("#edit-course").value }),
    });
    lectures = await api("/lectures");
    route();
  };
  $("#retry")?.addEventListener("click", async () => { await post(`/lectures/${l.id}/retry`); route(); });
  $("#regen-talk").onclick = async () => { await post(`/lectures/${l.id}/regenerate`); route(); };
  $("#regen-notes").onclick = async () => {
    if (confirm("Rewrite the notes and study talk from the transcript?")) { await post(`/lectures/${l.id}/regenerate?what=notes`); route(); }
  };
  $("#delete").onclick = async () => {
    if (confirm(`Delete “${l.title}” and its recording from this computer? This can't be undone.`)) {
      await api(`/lectures/${l.id}`, { method: "DELETE" });
      location.hash = "#/";
    }
  };
}

// ---------------------------------------------------------------- router
async function route() {
  const m = location.hash.match(/^#\/lecture\/([\w-]+)/);
  try {
    if (m) await renderLecture(m[1]); else await renderHome();
  } catch (e) {
    view.innerHTML = `<section class="card"><p class="error-text">Couldn't load: ${esc(e.message)}</p><a href="#/">Back home</a></section>`;
  }
}
window.addEventListener("hashchange", () => { window.scrollTo(0, 0); route(); });

// ---------------------------------------------------------------- status bar
async function refreshStatus() {
  let s;
  try { s = await api("/status"); } catch { return; }
  const chip = $("#ball-chip");
  const b = s.ball;
  if (b.recording) { chip.className = "chip rec"; chip.textContent = "Ball: recording"; }
  else if (b.reachable) { chip.className = "chip ok"; chip.textContent = b.mic ? "Ball: connected" : "Ball: connected · no mic"; }
  else { chip.className = "chip off"; chip.textContent = "Ball: not found"; }
  chip.title = b.lastError || (b.reachable ? `${s.ballUrl} · ${b.freeMb} MB free` : s.ballUrl);

  const missing = [];
  if (!s.setup.claudeKey) missing.push(`Claude API key: <code>dotnet user-secrets set "SmileyBall:AnthropicApiKey" "sk-ant-…"</code>`);
  if (!s.setup.fishKey) missing.push(`Fish Audio API key: <code>dotnet user-secrets set "SmileyBall:FishApiKey" "…"</code>`);
  if (!s.setup.ballToken) missing.push(`Ball token (printed by recorder.py): <code>dotnet user-secrets set "SmileyBall:BallToken" "…"</code>`);
  if (!s.setup.fishVoice) missing.push(`Optional: pick a voice on fish.audio and set <code>SmileyBall:FishVoiceId</code> (otherwise the default voice is used)`);
  const setup = $("#setup-banner");
  setup.hidden = missing.length === 0;
  setup.innerHTML = missing.length ? `<strong>Finish setup</strong> (then restart the app):<br>${missing.join("<br>")}` : "";
  if (missing.length === 1 && !s.setup.fishVoice) setup.className = "banner info"; else setup.className = "banner";

  const pb = $("#processing-banner");
  const activity = b.activity || (s.processing ? `Working on “${s.processing.title}”: ${STATUS_TEXT[s.processing.status] ?? s.processing.status}…` : "");
  pb.hidden = !activity;
  pb.textContent = activity + (s.pending > 1 ? ` (${s.pending - 1} more waiting)` : "");

  // Re-draw the page when processing moves on, so new notes show up by themselves.
  const key = JSON.stringify([s.processing, s.pending, b.activity]);
  if (key !== lastStatusKey) {
    const first = lastStatusKey === "";
    lastStatusKey = key;
    const playing = [...document.querySelectorAll("audio")].some(a => !a.paused);
    if (!first && !playing) route();
  }
}

$("#sync-btn").onclick = async () => {
  const btn = $("#sync-btn");
  btn.disabled = true; btn.textContent = "Syncing…";
  try { await post("/sync"); } finally { btn.disabled = false; btn.textContent = "Sync"; refreshStatus(); route(); }
};

$("#upload-input").onchange = async e => {
  const file = e.target.files[0];
  if (!file) return;
  const course = prompt("Which course is this recording for? (leave empty to let Smiley guess)") ?? "";
  const form = new FormData();
  form.append("file", file);
  form.append("course", course);
  const pb = $("#processing-banner");
  pb.hidden = false; pb.textContent = `Uploading ${file.name}…`;
  try {
    const { id } = await api("/upload", { method: "POST", body: form });
    location.hash = `#/lecture/${id}`;
  } catch (err) { alert("Upload failed: " + err.message); }
  e.target.value = "";
  refreshStatus();
};

// ---------------------------------------------------------------- ask
function updateScope() {
  const sel = $("#ask-scope");
  const current = sel.value;
  sel.innerHTML = `<option value="">all my lectures</option>` + lectures.filter(l => l.status === "ready")
    .map(l => `<option value="${l.id}">${esc(l.course)} · ${fmtDate(l.date)} · ${esc(l.title)}</option>`).join("");
  sel.value = current;
}

$("#ask-input").addEventListener("keydown", e => {
  if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); $("#ask-form").requestSubmit(); }
});

$("#ask-form").onsubmit = async e => {
  e.preventDefault();
  const input = $("#ask-input");
  const question = input.value.trim();
  if (!question) return;
  input.value = "";
  const chat = $("#chat");
  chat.querySelector("p.muted")?.remove();
  chat.insertAdjacentHTML("beforeend", `<div class="msg me">${esc(question)}</div>`);
  const bot = document.createElement("div");
  bot.className = "msg bot";
  bot.innerHTML = `<span class="muted">Thinking…</span>`;
  chat.appendChild(bot);
  chat.scrollTop = chat.scrollHeight;

  let answer = "";
  try {
    const res = await fetch("/api/ask", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ question, lectureId: $("#ask-scope").value || null, history: chatHistory }),
    });
    if (!res.ok) throw new Error(await res.text());
    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      answer += decoder.decode(value, { stream: true });
      bot.innerHTML = md(answer);
      chat.scrollTop = chat.scrollHeight;
    }
    chatHistory.push({ q: question, a: answer });
  } catch (err) {
    bot.innerHTML = `<span class="error-text">Sorry, that didn't work: ${esc(err.message)}</span>`;
  }
};

// ---------------------------------------------------------------- start
route();
refreshStatus();
setInterval(refreshStatus, 4000);
