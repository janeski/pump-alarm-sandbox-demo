const $ = (sel) => document.querySelector(sel);

const els = {
  drop: $("#drop"),
  file: $("#file"),
  fileInfo: $("#file-info"),
  fileName: $("#file-name"),
  fileMeta: $("#file-meta"),
  fileClear: $("#file-clear"),
  columns: $("#columns"),
  preview: $("#preview"),
  question: $("#question"),
  suggestions: $("#suggestions"),
  analyze: $("#analyze"),
  mode: $("#mode"),
  pipeline: $("#pipeline"),
  empty: $("#empty"),
  error: $("#error"),
  result: $("#result"),
  resultMeta: $("#result-meta"),
  resultQuestion: $("#result-question"),
  answer: $("#answer"),
  session: $("#session"),
  steps: $("#steps"),
};

let selectedFile = null;

// ---------- Sandbox status ----------

const sb = {
  root: $("#sandbox"),
  state: $("#sb-state"),
  detail: $("#sb-detail"),
  toggle: $("#sb-toggle"),
};

let sbTimer = null;
let sbPhase = null;
// After Pause/Resume, show "busy" until the state reaches the target (or 60 s pass).
let sbPending = null;

const phaseOf = (state) =>
  state === "Running" ? "running"
  : ["Stopped", "Suspended", "Paused"].includes(state) ? "paused"
  : "busy";

async function refreshSandbox() {
  clearTimeout(sbTimer);
  let next = 10_000;
  try {
    const res = await fetch("/sandbox");
    const s = await res.json();
    if (!res.ok) throw new Error(s.detail || res.statusText);
    if (!s.configured) { sb.root.hidden = true; return; }
    renderSandbox(s);
    if (sbPhase === "busy") next = 1_000;
  } catch (err) {
    renderSandboxError(err);
    next = 15_000;
  }
  sbTimer = setTimeout(refreshSandbox, next);
}

function renderSandbox(s) {
  let phase = phaseOf(s.state);
  if (sbPending) {
    if (phase === sbPending.target || Date.now() > sbPending.until) sbPending = null;
    else phase = "busy";
  }
  sbPhase = phase;

  sb.root.hidden = false;
  sb.root.dataset.phase = phase;
  sb.state.textContent = sbPending ? (sbPending.target === "running" ? "Resuming…" : "Pausing…") : s.state;
  sb.detail.textContent = phase === "paused" && s.stoppedReason ? `(${s.stoppedReason.toLowerCase()})` : "";

  const facts = [`Sandbox ${s.id}`];
  if (s.cpu || s.memory) facts.push(`${s.cpu ?? "?"} CPU, ${s.memory ?? "?"} memory`);
  if (s.autoSuspendSeconds) facts.push(`Pauses itself after ${Math.round(s.autoSuspendSeconds / 60)} min idle`);
  sb.root.title = facts.join("\n");

  sb.toggle.disabled = phase === "busy";
  sb.toggle.textContent = phase === "running" ? "Pause" : phase === "paused" ? "Resume" : "…";
}

function renderSandboxError(err) {
  sbPhase = "error";
  sb.root.hidden = false;
  sb.root.dataset.phase = "error";
  sb.state.textContent = "Unavailable";
  sb.detail.textContent = "";
  sb.root.title = err.message;
  sb.toggle.disabled = false;
  sb.toggle.textContent = "Retry";
}

sb.toggle.addEventListener("click", async () => {
  if (sbPhase === "error") return refreshSandbox();

  const action = sbPhase === "running" ? "stop" : "resume";
  sbPending = { target: action === "stop" ? "paused" : "running", until: Date.now() + 60_000 };
  sb.root.dataset.phase = "busy";
  sb.state.textContent = action === "stop" ? "Pausing…" : "Resuming…";
  sb.toggle.disabled = true;

  try {
    const res = await fetch(`/sandbox/${action}`, { method: "POST" });
    const s = await res.json();
    if (!res.ok) throw new Error(s.detail || res.statusText);
  } catch (err) {
    sbPending = null;
    renderSandboxError(err);
    return;
  }
  refreshSandbox();
});

document.addEventListener("visibilitychange", () => { if (!document.hidden) refreshSandbox(); });
refreshSandbox();

// ---------- File selection ----------

els.file.addEventListener("change", () => {
  if (els.file.files.length) setFile(els.file.files[0]);
});

["dragenter", "dragover"].forEach((type) =>
  els.drop.addEventListener(type, (e) => { e.preventDefault(); els.drop.classList.add("over"); }));
["dragleave", "drop"].forEach((type) =>
  els.drop.addEventListener(type, (e) => { e.preventDefault(); els.drop.classList.remove("over"); }));
els.drop.addEventListener("drop", (e) => {
  if (e.dataTransfer.files.length) setFile(e.dataTransfer.files[0]);
});

els.fileClear.addEventListener("click", () => {
  selectedFile = null;
  els.file.value = "";
  els.fileInfo.hidden = true;
  els.drop.hidden = false;
  updateButton();
});

async function setFile(file) {
  selectedFile = file;
  const text = await file.text();
  const lines = text.split(/\r?\n/).filter((l) => l.trim() !== "");
  const header = parseCsvLine(lines[0] ?? "");
  const rows = lines.slice(1, 6).map(parseCsvLine);

  els.fileName.textContent = file.name;
  els.fileMeta.textContent = `${formatBytes(file.size)} · ${Math.max(lines.length - 1, 0).toLocaleString()} alarms`;

  els.columns.replaceChildren(...header.map((h) => el("span", "chip", h)));

  const thead = el("thead");
  thead.append(el("tr", null, ...header.map((h) => el("th", null, h))));
  const tbody = el("tbody");
  rows.forEach((r) => tbody.append(el("tr", null, ...r.map((c) => el("td", null, c)))));
  els.preview.replaceChildren(thead, tbody);

  els.drop.hidden = true;
  els.fileInfo.hidden = false;
  updateButton();
}

// Small CSV line parser: handles quoted fields and escaped quotes. Good enough for a preview.
function parseCsvLine(line) {
  const out = [];
  let cur = "";
  let quoted = false;
  for (let i = 0; i < line.length; i++) {
    const c = line[i];
    if (quoted) {
      if (c === '"' && line[i + 1] === '"') { cur += '"'; i++; }
      else if (c === '"') quoted = false;
      else cur += c;
    } else if (c === '"') quoted = true;
    else if (c === ",") { out.push(cur); cur = ""; }
    else cur += c;
  }
  out.push(cur);
  return out;
}

// ---------- Question ----------

els.suggestions.addEventListener("click", (e) => {
  if (e.target.matches(".chip")) {
    els.question.value = e.target.textContent;
    els.question.focus();
  }
});
els.question.addEventListener("input", updateButton);

function updateButton() {
  els.analyze.disabled = !selectedFile || !els.question.value.trim() || els.analyze.classList.contains("busy");
}

// ---------- Analyze ----------

els.analyze.addEventListener("click", analyze);

async function analyze() {
  els.analyze.classList.add("busy");
  els.analyze.querySelector(".btn-label").textContent = "Analyzing…";
  updateButton();
  els.error.hidden = true;
  els.empty.hidden = true;
  els.result.hidden = true;
  setPipeline("active");

  // The server resumes a paused sandbox before running anything; show that in the badge.
  if (sbPhase === "paused") {
    sbPending = { target: "running", until: Date.now() + 60_000 };
    refreshSandbox();
  }

  const form = new FormData();
  form.append("file", selectedFile);
  form.append("question", els.question.value.trim());
  const started = performance.now();

  try {
    const res = await fetch("/analyze", { method: "POST", body: form });
    if (!res.ok) throw new Error(`${res.status} ${res.statusText}\n${await res.text()}`);
    const data = await res.json();
    renderResult(data, performance.now() - started);
    setPipeline("done");
  } catch (err) {
    els.error.textContent = `The analysis failed.\n\n${err.message}`;
    els.error.hidden = false;
    setPipeline("idle");
  } finally {
    els.analyze.classList.remove("busy");
    els.analyze.querySelector(".btn-label").textContent = "Analyze in sandbox";
    updateButton();
    refreshSandbox();
  }
}

// The API answers in one response, so the pipeline only shows "working" or "done".
// When the API streams progress (server-sent events), each step can light up on its own.
function setPipeline(state) {
  els.pipeline.querySelectorAll("li").forEach((li) => {
    li.classList.toggle("active", state === "active");
    li.classList.toggle("done", state === "done");
  });
}

function renderResult(data, ms) {
  els.mode.hidden = false;
  els.mode.className = `badge ${data.mode === "live" ? "live" : "stub"}`;
  els.mode.textContent =
    data.mode === "stub" ? "Stub data, no sandbox yet"
    : "Live sandbox";

  els.resultQuestion.textContent = data.question;
  els.answer.innerHTML = renderMarkdown(data.answer);
  els.resultMeta.textContent = `${data.steps.length} code run${data.steps.length === 1 ? "" : "s"} · ${(ms / 1000).toFixed(1)} s`;
  els.session.textContent = `session ${data.sessionId}`;

  els.steps.replaceChildren(...data.steps.map((step, i) => renderStep(step, i)));
  renderTrace(data.trace);
  renderGovernance(data.audit, data.policy);
  els.result.hidden = false;
}

// Audit trail of the Agent Governance Toolkit: one row per run_python attempt.
function renderGovernance(audit, policy) {
  const section = $("#gov-section");
  if (!audit || !audit.length) { section.hidden = true; return; }

  $("#gov-policy").textContent = policy ?? "policy";
  const blocked = audit.filter((a) => !a.allowed).length;
  $("#gov-meta").textContent =
    `${audit.length} check${audit.length === 1 ? "" : "s"} · ${blocked} blocked`;

  $("#gov-rows").replaceChildren(...audit.map((a) => {
    const time = new Date(a.time).toLocaleTimeString();
    const decision = el("span", `decision ${a.allowed ? "allow" : "block"}`, a.allowed ? "allowed" : "blocked");
    const sandbox = a.allowed
      ? `exit ${a.exitCode ?? "?"}${a.sandboxMs != null ? ` · ${a.sandboxMs} ms` : ""}`
      : "not run";
    const row = el("tr", a.allowed ? null : "blocked",
      el("td", "mono", `#${a.run}`),
      el("td", null, decision),
      el("td", "mono", a.rule ?? "(default)"),
      el("td", "facts", a.facts),
      el("td", "mono", a.codeSha256),
      el("td", "mono", sandbox));
    row.title = `${time}\n${a.reason}`;
    return row;
  }));
  section.hidden = false;
}

// Waterfall of the spans the server recorded: model calls, tool calls, sandbox calls.
function renderTrace(spans) {
  const section = $("#trace-section");
  if (!spans || !spans.length) { section.hidden = true; return; }

  const total = Math.max(...spans.map((s) => s.startMs + s.durationMs), 1);
  const kindOf = (name) =>
    name.startsWith("chat") ? "model"
    : name.startsWith("execute_tool") ? "tool"
    : name.startsWith("governance") ? "governance"
    : name.startsWith("sandbox") ? "sandbox"
    : "";
  const ms = (v) => (v >= 1000 ? `${(v / 1000).toFixed(1)} s` : `${Math.round(v)} ms`);

  $("#trace").replaceChildren(...spans.map((s) => {
    const name = el("div", "trace-name", el("b", null, s.name));
    name.style.paddingLeft = `${s.depth * 14}px`;
    if (s.detail) name.append(el("span", "trace-detail", s.detail));
    name.title = s.detail ? `${s.name}\n${s.detail}` : s.name;

    const bar = el("div", `trace-bar ${kindOf(s.name)}${s.error ? " error" : ""}`);
    bar.style.left = `${(s.startMs / total) * 100}%`;
    bar.style.width = `${(s.durationMs / total) * 100}%`;

    return el("div", "trace-row", name, el("div", "trace-track", bar), el("div", "trace-ms", ms(s.durationMs)));
  }));

  $("#trace-meta").textContent = `${spans.length} spans · ${ms(total)}`;
  section.hidden = false;
}

function renderStep(step, i) {
  const failed = Boolean(step.stderr && step.stderr.trim());
  const details = el("details", step.blocked ? "step blocked" : "step");
  details.open = i === 0 || step.blocked;

  const status = step.blocked
    ? el("span", "status block", `blocked · ${step.rule ?? "policy"}`)
    : el("span", `status ${failed ? "fail" : "ok"}`, failed ? "error" : "ok");
  const firstLine = step.code.split("\n").find((l) => l.trim()) ?? "";
  details.append(el("summary", null, el("span", null, `Run ${i + 1}`), status, el("span", "first-line", firstLine)));

  if (step.blocked)
    details.append(el("div", "policy-note",
      "Not run: the Agent Governance Toolkit policy blocked this script before it reached the sandbox. ",
      el("span", "muted", step.reason ?? "")));

  details.append(el("div", "block-label", "Python"), el("pre", null, step.code.trimEnd()));
  if (step.stdout && step.stdout.trim())
    details.append(el("div", "block-label", "Output"), el("pre", "out", step.stdout.trimEnd()));
  if (failed)
    details.append(el("div", "block-label", "Error"), el("pre", "out err", step.stderr.trimEnd()));
  return details;
}

// ---------- Helpers ----------

function el(tag, className, ...children) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  children.forEach((c) => node.append(c));
  return node;
}

function formatBytes(n) {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}

// Tiny markdown renderer for the model's answer: headings, lists, paragraphs, **bold**, `code`.
// Text is escaped first, so the model cannot inject HTML.
function renderMarkdown(md) {
  const escape = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  const inline = (s) => escape(s)
    .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
    .replace(/`([^`]+)`/g, "<code>$1</code>");

  let html = "";
  let list = null;
  const closeList = () => { if (list) { html += `</${list}>`; list = null; } };
  const openList = (tag) => { if (list !== tag) { closeList(); html += `<${tag}>`; list = tag; } };

  for (const raw of md.split(/\r?\n/)) {
    const line = raw.trimEnd();
    let m;
    if ((m = line.match(/^#{1,4}\s+(.*)/))) { closeList(); html += `<h3>${inline(m[1])}</h3>`; }
    else if ((m = line.match(/^\s*[-*]\s+(.*)/))) { openList("ul"); html += `<li>${inline(m[1])}</li>`; }
    else if ((m = line.match(/^\s*\d+[.)]\s+(.*)/))) { openList("ol"); html += `<li>${inline(m[1])}</li>`; }
    else if (!line.trim()) closeList();
    else { closeList(); html += `<p>${inline(line)}</p>`; }
  }
  closeList();
  return html;
}
