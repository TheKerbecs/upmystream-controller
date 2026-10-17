"use strict";
// The page shows state and sends requests; the host (MainForm.cs) owns config, the stream and input.

const $ = (s) => document.querySelector(s);
const $$ = (s) => [...document.querySelectorAll(s)];
const host = window.chrome && window.chrome.webview ? window.chrome.webview : devHost();
const send = (type, extra = {}) => host.postMessage({ type, ...extra });

let state = null;
let editing = null; // the trigger in the editor
let capture = null; // { target: "trigger" | "hotkey", mods: Set, other: bool }

const STATUS = {
  stopped: "Stopped", connecting: "Connecting…", connected: "Connected",
  reconnecting: "Reconnecting…", badkey: "Key rejected",
};

// ---------- rendering ----------

host.addEventListener("message", (e) => {
  const m = e.data;
  if (m.type === "state") return render(m);
  if (m.type === "key-test") {
    note($("#key-note"), m.ok ? m.text : m.error, m.ok);
    $("#key-save").disabled = false;
    if (m.ok) $("#key").value = "";
  }
  if (m.type === "keys-checked") captured(m);
  if (m.type === "hotkey-set") {
    stopCapture();
    if (!m.ok) toast(m.error);
  }
});

function render(s) {
  state = s;
  const pill = $("#status");
  pill.className = "pill " + s.status;
  pill.textContent = STATUS[s.status] || s.status;
  pill.title = s.detail || "";

  $("#run").textContent = s.running ? "Stop" : "Start";
  $("#run").disabled = !s.keyHint;
  $("#power").classList.toggle("off", s.paused);
  $("#power-label").textContent = s.paused ? "Off" : "On";
  $("#power-hotkey").textContent = s.hotkey;
  if (!capture || capture.target !== "hotkey") $("#hotkey").textContent = s.hotkey;

  $("#key-field").classList.toggle("hidden", !!s.keyHint);
  $("#key-set").classList.toggle("hidden", !s.keyHint);
  $("#key-hint").textContent = s.keyHint;
  $("#chat").checked = s.chat;

  const games = ["", ...s.games];
  if (s.game && !games.includes(s.game)) games.push(s.game);
  const sel = $("#game");
  const opts = games.map((g) => g || "(any window)").join("|");
  if (sel.dataset.opts !== opts) {
    sel.dataset.opts = opts;
    sel.replaceChildren(...games.map((g) => new Option(g || "(any window)", g)));
  }
  sel.value = s.game || "";

  $("#refresh").disabled = !s.keyHint;
  renderTriggers(s);
  $("#log").replaceChildren(...s.log.map((line) => {
    const li = document.createElement("li");
    const cut = line.indexOf("   ");
    const t = document.createElement("time");
    t.textContent = line.slice(0, cut);
    li.append(t, line.slice(cut + 3));
    return li;
  }));
}

function renderTriggers(s) {
  const box = $("#triggers");
  box.replaceChildren();
  const hasChat = s.triggers.some((v) => v.trigger.source === "chat");
  if (hasChat && !s.chat) {
    const p = document.createElement("p");
    p.className = "chat-off-note";
    p.textContent = "Chat triggers are off. Turn them on in Setup.";
    box.append(p);
  }
  if (!s.triggers.length) {
    const p = document.createElement("p");
    p.className = "empty";
    p.textContent = s.keyHint
      ? "No triggers yet. Add one, or create redeems on upmy.stream and click ↻."
      : "Connect your API key to load your redeems. You can add chat triggers without one.";
    box.append(p);
    return;
  }
  for (const v of s.triggers) {
    const t = v.trigger;
    const row = document.createElement("div");
    row.className = "trigger" + (t.enabled && (t.source !== "chat" || s.chat) ? "" : " off");

    const sw = document.createElement("label");
    sw.className = "switch";
    sw.title = t.enabled ? "On" : "Off";
    const box2 = document.createElement("input");
    box2.type = "checkbox";
    box2.setAttribute("aria-label", `${t.source === "chat" ? "Chat" : "Redeem"} trigger ${t.match || ""}`);
    box2.checked = t.enabled;
    box2.onchange = () => send("save-trigger", { trigger: { ...t, enabled: box2.checked } });
    sw.append(box2, document.createElement("span"));

    const badge = document.createElement("span");
    badge.className = "badge " + t.source;
    badge.textContent = t.source === "chat" ? "Chat" : "Redeem";

    const what = document.createElement("div");
    what.className = "what";
    const match = document.createElement("div");
    match.className = "match";
    match.textContent = t.match || "(no name)";
    const sum = document.createElement("div");
    sum.className = "summary" + (v.problem ? " bad" : "");
    sum.textContent = v.problem ? cap(v.problem) : v.summary;
    what.append(match, sum);

    const cd = document.createElement("span");
    cd.className = "cd";
    cd.textContent = t.cooldownS ? `${t.cooldownS} s cooldown` : "";

    const tools = document.createElement("div");
    tools.className = "tools";
    const edit = button("Edit", "ghost", () => openEditor(t));
    const del = button("Delete", "danger", () => send("delete-trigger", { id: t.id }));
    tools.append(edit, del);

    row.append(sw, badge, what, cd, tools);
    box.append(row);
  }
}

// ---------- setup ----------

$("#key-save").onclick = () => {
  const key = $("#key").value.trim();
  if (!key) return note($("#key-note"), "Paste your key first.", false);
  $("#key-save").disabled = true;
  note($("#key-note"), "Checking…", null);
  send("set-key", { text: key });
};
$("#key").onkeydown = (e) => { if (e.key === "Enter") $("#key-save").click(); };
$("#key-change").onclick = () => {
  $("#key-set").classList.add("hidden");
  $("#key-field").classList.remove("hidden");
  $("#key").focus();
};
$("#game").onchange = () => send("set-game", { text: $("#game").value });
$("#games-refresh").onclick = () => send("list-games");
$("#chat").onchange = () => send("set-chat", { on: $("#chat").checked });
$("#run").onclick = () => send(state && state.running ? "stop" : "start");
$("#power").onclick = () => send("pause", { on: !(state && state.paused) });
$("#hotkey").onclick = () => startCapture("hotkey");
$("#add").onclick = () => openEditor(null);
$("#refresh").onclick = () => send("refresh-redeems");

// ---------- editor ----------

const editor = $("#editor");

function seg(name, value) {
  for (const b of $$(`.seg[data-name=${name}] button`)) b.classList.toggle("on", b.dataset.value === value);
  if (name === "action") for (const p of $$(".pane")) p.classList.toggle("hidden", p.dataset.pane !== value);
  if (name === "source") {
    $("#redeem").classList.toggle("hidden", value !== "redeem");
    $("#match").classList.toggle("hidden", value === "redeem");
    const chatOff = value === "chat" && state && !state.chat;
    note($("#match-note"), value === "redeem"
      ? "Don't see your redeem? Create it on upmy.stream, then click ↻ next to Add trigger."
      : chatOff ? "Chat triggers are turned off in Setup, so this one won't fire until you turn them on."
      : "Fires when a chat message is exactly this text. Upper and lower case don't matter.", null);
    $("#match-note").classList.toggle("warn", chatOff);
  }
}
for (const b of $$(".seg button")) b.onclick = () => seg(b.parentElement.dataset.name, b.dataset.value);
const segValue = (name) => ($(`.seg[data-name=${name}] button.on`) || {}).dataset?.value;

function openEditor(t) {
  editing = t ? { ...t } : {
    id: "", enabled: true, source: "redeem", match: "", action: "key", keys: "", button: "Left",
    holdMs: 50, notches: -1, dx: 0, dy: 0, moveMs: 200, cooldownS: 0,
  };
  const e = editing;
  $("#editor-title").textContent = t ? "Edit trigger" : "Add trigger";
  fillRedeems(e.source === "redeem" ? e.match : "");
  seg("source", e.source);
  seg("action", e.action);
  $("#match").value = e.source === "chat" ? e.match : "";
  $("#capture").textContent = e.action === "key" && e.keys ? e.keys : "Click to set";
  $("#capture").dataset.keys = e.action === "key" ? e.keys : "";
  $("#hold").value = e.holdMs;
  $("#hold-mouse").value = e.holdMs;
  $("#button").value = e.button || "Left";
  const mods = e.action === "mouse" ? (e.keys || "").split("+") : [];
  for (const c of $$(".checks input")) c.checked = mods.includes(c.value);
  $("#scroll-dir").value = e.notches > 0 ? "1" : "-1";
  $("#notches").value = Math.abs(e.notches) || 1;
  $("#dx").value = e.dx ?? 0;
  $("#dy").value = e.dy ?? 0;
  $("#move-ms").value = e.moveMs ?? 200;
  $("#cooldown").value = e.cooldownS;
  $("#editor-error").textContent = "";
  editor.showModal();
  (e.source === "redeem" ? $("#redeem") : $("#match")).focus();
}

// The redeems on upmy.stream, plus the trigger's own one if it was deleted there.
function fillRedeems(current) {
  const names = state ? [...state.redeems] : [];
  const sel = $("#redeem");
  const opts = names.map((n) => new Option(n, n));
  if (current && !names.some((n) => n.toLowerCase() === current.toLowerCase()))
    opts.unshift(new Option(current + " (deleted on upmy.stream)", current));
  if (!opts.length) opts.push(new Option("No redeems found. Create one on upmy.stream, then click ↻.", ""));
  sel.replaceChildren(...opts);
  sel.disabled = !opts[0].value;
  const match = names.find((n) => current && n.toLowerCase() === current.toLowerCase());
  sel.value = match || current || opts[0].value;
}

const num = (sel) => Math.round(Number($(sel).value) || 0);

function readEditor() {
  const source = segValue("source");
  const action = segValue("action");
  const match = source === "redeem" ? $("#redeem").value : $("#match").value.trim();
  const t = { ...editing, source, action, match, cooldownS: Math.max(0, num("#cooldown")) };
  if (action === "key") { t.keys = $("#capture").dataset.keys || ""; t.holdMs = num("#hold"); }
  if (action === "mouse") {
    t.button = $("#button").value;
    t.keys = $$(".checks input").filter((c) => c.checked).map((c) => c.value).join("+");
    t.holdMs = num("#hold-mouse");
  }
  if (action === "scroll") t.notches = Number($("#scroll-dir").value) * Math.max(1, num("#notches"));
  if (action === "move") { t.dx = num("#dx"); t.dy = num("#dy"); t.moveMs = Math.max(0, num("#move-ms")); }
  return t;
}

function problem(t, forTry) {
  if (!forTry && !t.match) return t.source === "chat" ? "Type the chat message that should trigger this." : "Pick a redeem.";
  if (t.action === "key" && !t.keys) return "Click the Keys box and press a key or combo.";
  if ((t.action === "key" || t.action === "mouse") && (t.holdMs < 1 || t.holdMs > 10000)) return "Hold time must be between 1 and 10000 ms.";
  if (t.action === "scroll" && Math.abs(t.notches) > 20) return "Scroll between 1 and 20 notches.";
  if (t.action === "move") {
    if (!t.dx && !t.dy) return "Enter how far to move.";
    if (Math.abs(t.dx) > 5000 || Math.abs(t.dy) > 5000) return "You can move at most 5000 px in each direction.";
    if (t.moveMs > 5000) return "The move can take at most 5000 ms.";
  }
  return "";
}

$("#editor-form").onsubmit = (ev) => {
  ev.preventDefault();
  const t = readEditor();
  const p = problem(t, false);
  if (p) return ($("#editor-error").textContent = p);
  send("save-trigger", { trigger: t });
  editor.close();
  toast("Saved");
};
$("#cancel").onclick = () => editor.close();
editor.addEventListener("close", () => { if (capture && capture.target === "trigger") stopCapture(); });

$("#try").onclick = () => {
  const t = readEditor();
  const p = problem(t, true);
  if (p) return ($("#editor-error").textContent = p);
  $("#editor-error").textContent = "";
  const btn = $("#try");
  let n = 3;
  btn.disabled = true;
  btn.classList.add("counting");
  const tick = () => {
    if (n === 0) {
      send("run-now", { trigger: t });
      btn.textContent = "Try it";
      btn.disabled = false;
      btn.classList.remove("counting");
      return;
    }
    btn.textContent = `Switch to your game… ${n}`;
    n--;
    setTimeout(tick, 1000);
  };
  tick();
};

// ---------- key capture (trigger keys and the hotkey) ----------

const CODES = {
  Space: "Space", Enter: "Enter", NumpadEnter: "Enter", Tab: "Tab", Escape: "Esc", Backspace: "Backspace",
  CapsLock: "CapsLock", ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right",
  Insert: "Insert", Delete: "Delete", Home: "Home", End: "End", PageUp: "PageUp", PageDown: "PageDown",
  NumpadMultiply: "NumMultiply", NumpadAdd: "NumAdd", NumpadSubtract: "NumSubtract", NumpadDecimal: "NumDecimal",
  NumpadDivide: "NumDivide", Semicolon: "Semicolon", Equal: "Equals", Comma: "Comma", Minus: "Minus",
  Period: "Period", Slash: "Slash", Backquote: "Backquote", BracketLeft: "LeftBracket", Backslash: "Backslash",
  BracketRight: "RightBracket", Quote: "Quote",
};
const MODS = { ShiftLeft: "Shift", ShiftRight: "Shift", ControlLeft: "Ctrl", ControlRight: "Ctrl", AltLeft: "Alt", AltRight: "Alt" };
const ORDER = ["Ctrl", "Shift", "Alt"];

function codeName(code) {
  let m;
  if ((m = /^Key([A-Z])$/.exec(code))) return m[1];
  if ((m = /^Digit(\d)$/.exec(code))) return m[1];
  if ((m = /^Numpad(\d)$/.exec(code))) return "Num" + m[1];
  if (/^F([1-9]|1\d|2[0-4])$/.test(code)) return code;
  return CODES[code] || null;
}

function startCapture(target) {
  if (capture) stopCapture();
  capture = { target, mods: new Set(), other: false };
  const el = target === "hotkey" ? $("#hotkey") : $("#capture");
  el.classList.add("listening");
  el.textContent = "Press a key… (Esc to cancel)";
  if (target === "trigger") $("#editor-error").textContent = "";
  send("capture", { on: true });
}

function stopCapture() {
  if (!capture) return;
  const target = capture.target;
  if (target === "trigger") $("#editor-error").textContent = "";
  capture = null;
  send("capture", { on: false });
  const el = target === "hotkey" ? $("#hotkey") : $("#capture");
  el.classList.remove("listening");
  el.textContent = target === "hotkey" ? (state ? state.hotkey : "") : ($("#capture").dataset.keys || "Click to set");
}

function finish(names) {
  const text = ORDER.filter((m) => names.includes(m)).concat(names.filter((n) => !ORDER.includes(n))).join("+");
  if (capture.target === "hotkey") send("set-hotkey", { text });
  else send("check-keys", { text });
}

function captured(m) {
  if (!capture) return;
  if (capture.target === "hotkey") { // only Alt+F4 lands here: the host kept the window open
    toast(cap(m.error));
    capture.mods.clear();
    capture.other = true;
    return;
  }
  if (m.ok) {
    $("#capture").dataset.keys = m.text;
    stopCapture();
  } else {
    $("#editor-error").textContent = cap(m.error) + ". Try another one or press Esc to cancel.";
    capture.mods.clear();
    capture.other = m.text === "Alt+F4"; // the Alt still going up must not count as "Alt" alone
  }
}

$("#capture").onclick = () => startCapture("trigger");

window.addEventListener("keydown", (e) => {
  if (!capture) return;
  e.preventDefault();
  e.stopPropagation();
  if (e.repeat) return;
  if (e.code === "Escape" && !e.ctrlKey && !e.shiftKey && !e.altKey) return stopCapture();
  if (MODS[e.code]) return void capture.mods.add(MODS[e.code]);
  capture.other = true;
  const name = codeName(e.code);
  if (!name) {
    if (capture.target === "trigger") $("#editor-error").textContent = "That key isn't supported. Try another one or press Esc to cancel.";
    else toast("That key isn't supported");
    return;
  }
  const mods = [];
  if (e.ctrlKey) mods.push("Ctrl");
  if (e.shiftKey) mods.push("Shift");
  if (e.altKey) mods.push("Alt");
  finish([...mods, name]);
}, true);

window.addEventListener("keyup", (e) => {
  if (!capture) return;
  e.preventDefault();
  if (!MODS[e.code]) return;
  if (capture.other) { // a modifier let go after an unusable key: start over
    if (!e.ctrlKey && !e.shiftKey && !e.altKey) capture.other = false;
    capture.mods.clear();
    return;
  }
  if (capture.target === "hotkey") return; // a hotkey needs a main key
  const held = [...capture.mods];
  capture.mods.clear();
  finish(held); // a modifier alone, e.g. Shift to sprint
}, true);

// ---------- small helpers ----------

function button(text, cls, onclick) {
  const b = document.createElement("button");
  b.type = "button";
  b.className = cls;
  b.textContent = text;
  b.onclick = onclick;
  return b;
}
function note(el, text, ok) {
  el.textContent = text || "";
  el.classList.toggle("ok", ok === true);
  el.classList.toggle("bad", ok === false);
}
const cap = (s) => (s ? s[0].toUpperCase() + s.slice(1) : "");
let toastTimer;
function toast(text) {
  const t = $("#toast");
  t.textContent = text;
  t.classList.add("show");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove("show"), 1800);
}
document.addEventListener("contextmenu", (e) => { if (!e.target.closest("input")) e.preventDefault(); });

send("ready");

// ---------- browser preview without the host (development only) ----------

function devHost() {
  const listeners = [];
  const s = {
    type: "state", version: "dev", keyHint: "••••7Qx2", status: "connected", detail: "", running: true, paused: false,
    chat: true, hotkey: "Ctrl+Shift+F12", game: "eldenring", games: ["discord", "eldenring", "obs64", "spotify"],
    redeems: ["fireball", "jump", "sprint", "spin"],
    triggers: [
      { trigger: { id: "a", enabled: true, source: "redeem", match: "fireball", action: "key", keys: "Shift+Q", holdMs: 50, cooldownS: 10 }, summary: "Shift+Q · 50 ms", problem: "" },
      { trigger: { id: "b", enabled: true, source: "chat", match: "w", action: "key", keys: "W", holdMs: 400, cooldownS: 2 }, summary: "W · 400 ms", problem: "" },
      { trigger: { id: "c", enabled: true, source: "chat", match: "super", action: "key", keys: "F", holdMs: 50, cooldownS: 2 }, summary: "F · 50 ms", problem: "" },
      { trigger: { id: "d", enabled: true, source: "redeem", match: "spin", action: "move", dx: 1800, dy: 0, moveMs: 600, cooldownS: 30 }, summary: "Move 1800, 0 · 600 ms", problem: "" },
      { trigger: { id: "e", enabled: true, source: "chat", match: "shoot", action: "mouse", button: "Left", keys: "", holdMs: 50, cooldownS: 2 }, summary: "Left click · 50 ms", problem: "" },
      { trigger: { id: "f", enabled: false, source: "redeem", match: "jump", action: "key", keys: "", holdMs: 50, cooldownS: 0 }, summary: "", problem: "no key set" },
    ],
    log: ["21:42:10   amy (chat) → w → W · 400 ms", "21:42:08   bob → fireball → Shift+Q · 50 ms", "21:41:55   bob → fireball: skipped, cooldown", "21:40:02   Connected"],
  };
  setTimeout(() => listeners.forEach((f) => f({ data: s })), 0);
  return {
    postMessage: (m) => console.log("→ host", m),
    addEventListener: (_, f) => listeners.push(f),
  };
}
