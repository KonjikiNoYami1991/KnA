// LLM assistant: natural language → settings, and vision analysis of a job's frames.
import { $, esc, state, t, api, post, toast, guard, label } from "./core.js";
import { cloneSettings } from "./form.js";

let onApply = null, lastSuggestion = null, jobId = null;

/** Opens the assistant. apply(settings) receives the accepted settings. */
export function openAssistant(apply, forJob = null) {
  onApply = apply; jobId = forJob; lastSuggestion = null;
  const ai = state.settings.ai ?? {};
  $("#ai-model").textContent = ai.model ? `${ai.provider} · ${ai.model}` : t("No model chosen: Settings → AI");
  $("#ai-result").innerHTML = "";
  $("#ai-status").textContent = "";
  $("#ai-setup-row").hidden = !!ai.model;
  $("#dlg-ai").showModal();
  $("#ai-prompt").focus();
}

/** Runs the vision analysis of a job and shows the proposal in the same dialog. */
export async function analyzeJob(id, current, apply) {
  onApply = apply; jobId = id; lastSuggestion = null;
  $("#ai-model").textContent = (state.settings.ai?.visionModel || state.settings.ai?.model) ?? "";
  $("#ai-result").innerHTML = `<p class="muted">${esc(t("Extracting frames and asking the vision model… (this can take a minute on a local model)"))}</p>`;
  $("#dlg-ai").showModal();
  try { show(await post(`/api/jobs/${id}/ai/analyze`), current); }
  catch (e) { $("#ai-result").innerHTML = `<p class="issue err">${esc(e.message)}</p>`; }
}

$("#ai-close").onclick = () => $("#dlg-ai").close();
$("#ai-ask").onclick = guard(async () => {
  const prompt = $("#ai-prompt").value.trim();
  if (!prompt) return;
  const current = cloneSettings(state.form);
  $("#ai-status").textContent = t("Thinking…");
  $("#ai-ask").disabled = true;
  try { show(await post("/api/ai/suggest", { prompt, settings: current, jobId }), current); }
  finally { $("#ai-ask").disabled = false; $("#ai-status").textContent = ""; }
});

function show(r, before) {
  lastSuggestion = r;
  const o = state.options, s = r.settings;
  const rows = [
    [t("Profile"), label(o.profiles, s.profile)], [t("Video codec"), label(o.codecs, s.codec)], [t("Encoder"), label(o.backends, s.backend)],
    [t("Quality"), label(o.qualities, s.quality)], [t("Resolution"), label(o.resolutions, s.resolution)], [t("Subtitles"), label(o.subtitleModes, s.subtitles)],
    [t("Audio"), label(o.audioCodecs, s.audioCodec)], [t("Upscaling / AI"), label(o.upscaleMethods, s.upscale?.method ?? "Standard") + (s.upscale?.model ? " · " + s.upscale.model : "")],
    [t("Filters"), [s.videoFilters.denoise !== "Off" && "denoise " + s.videoFilters.denoise, s.videoFilters.deband !== "Off" && "deband " + s.videoFilters.deband,
      s.videoFilters.sharpen !== "Off" && "sharpen " + s.videoFilters.sharpen].filter(Boolean).join(", ") || "—"],
  ];
  $("#ai-result").innerHTML = `
    ${r.contentType ? `<p><span class="chip hw">${esc(r.contentType)}</span></p>` : ""}
    ${r.explanation ? `<p>${esc(r.explanation)}</p>` : ""}
    <table class="tracks ai-table">${rows.map(([k, v]) => `<tr><td class="muted">${esc(k)}</td><td><b>${esc(v)}</b></td></tr>`).join("")}</table>
    ${r.warnings.map(w => `<div class="issue warn">${esc(w)}</div>`).join("")}
    <div class="row" style="margin-top:12px">
      <button class="btn primary" id="ai-apply">${esc(t("Use these settings"))}</button>
      <span class="muted small">${esc(t("{0} change(s) proposed", r.changes.length))}</span>
    </div>`;
  $("#ai-apply").onclick = () => {
    onApply?.(cloneSettings(lastSuggestion.settings));
    $("#dlg-ai").close();
    toast(t("AI settings applied"), "ok");
  };
}

$("#ai-setup").onclick = () => openAiSetup(() => {
  const ai = state.settings.ai ?? {};
  $("#ai-model").textContent = ai.model ? `${ai.provider} · ${ai.model}` : t("No model chosen: Settings → AI");
  $("#ai-setup-row").hidden = !!ai.model;
});

// ---------------------------------------------------------------- guided set-up (same steps as the desktop one)
const ais = { step: 0, ai: null, key: "", check: null, guide: null, done: null };
const AIS_STEPS = 5;

/** Opens the guided set-up; done() runs after it saved. */
export function openAiSetup(done = null) {
  ais.step = 0; ais.ai = { ...(state.settings.ai ?? { provider: "ollama" }) }; ais.key = ""; ais.check = null; ais.done = done;
  renderAiSetup();
  $("#dlg-ai-setup").showModal();
}
const aiBody = () => ({ ...ais.ai, apiKey: ais.key });

async function renderAiSetup() {
  const b = $("#ais-body"), a = ais.ai;
  $("#ais-step").textContent = `${ais.step + 1} / ${AIS_STEPS}`;
  $("#ais-back").disabled = ais.step === 0;
  $("#ais-next").textContent = ais.step === AIS_STEPS - 1 ? t("Save") : t("Next");
  const checkHtml = c => c ? `<p class="issue ${c.ok ? "info" : "err"}">${c.ok ? "✔" : "✖"} ${esc(c.message)}</p>${c.hint ? `<p class="muted small">${esc(c.hint)}</p>` : ""}` : "";
  if (ais.step === 0) {
    b.innerHTML = `<h3 style="margin-top:0">${esc(t("Which program runs the model?"))}</h3>
      <p class="muted small">${esc(t("The assistant uses a language model on your computer (or a server you choose): nothing is sent outside unless you pick a cloud service."))}</p>
      <div class="choices">${state.options.llmProviders.map(p => `<button type="button" class="choice ${a.provider === p.value ? "on" : ""}" data-v="${esc(p.value)}"><b>${esc(p.label)}</b>
        <span>${esc(p.value === "ollama" ? t("Recommended: the simplest, free") : p.description ?? "")}</span></button>`).join("")}</div>`;
    b.onclick = e => { const c = e.target.closest(".choice"); if (!c) return; if (a.provider !== c.dataset.v) { a.provider = c.dataset.v; a.baseUrl = null; ais.check = null; } renderAiSetup(); };
    b.ondblclick = e => { if (e.target.closest(".choice")) $("#ais-next").click(); };
    return;
  }
  b.onclick = b.ondblclick = null;
  if (ais.step === 1) {
    const g = ais.guide?.provider === a.provider ? ais.guide : { provider: a.provider, ...(await api("GET", `/api/ai/setup/guide?provider=${encodeURIComponent(a.provider)}`)) };
    ais.guide = g;
    b.innerHTML = `<h3 style="margin-top:0">${esc(t("Install and start it"))}</h3>
      <div class="section"><ol style="margin:0;padding-left:20px;display:grid;gap:4px">${g.steps.map(x => `<li>${esc(x)}</li>`).join("")}</ol></div>
      <label class="field"><span>${esc(t("Address"))}</span><input id="ais-url" class="mono" value="${esc(a.baseUrl ?? "")}" placeholder="${esc(g.url ?? "")}"><small class="muted">${esc(t("Empty = the usual address of this program."))}</small></label>
      ${g.needsKey || a.hasApiKey ? `<label class="field"><span>${esc(t("API key"))}</span><input id="ais-key" type="password" value="" placeholder="${esc(a.hasApiKey ? t("A key is stored. Leave empty to keep it, \"-\" to delete it.") : "")}"></label>` : ""}
      <button class="btn primary sm" type="button" id="ais-check">${esc(t("Check the connection"))}</button>
      <div id="ais-result">${checkHtml(ais.check)}</div>`;
    b.oninput = e => { if (e.target.id === "ais-url") { a.baseUrl = e.target.value.trim() || null; ais.check = null; } if (e.target.id === "ais-key") { ais.key = e.target.value; ais.check = null; } };
    $("#ais-check").onclick = guard(async () => {
      $("#ais-result").innerHTML = `<p class="muted small">${esc(t("Checking…"))}</p>`;
      ais.check = await post("/api/ai/setup/check", aiBody());
      $("#ais-result").innerHTML = checkHtml(ais.check);
    });
    return;
  }
  b.oninput = null;
  if (ais.step === 2 || ais.step === 3) {
    const vision = ais.step === 3, key = vision ? "visionModel" : "model";
    const models = (vision ? ais.check?.visionModels : ais.check?.models) ?? [];
    if (a[key] == null) a[key] = vision ? models[0] ?? null : models.find(m => !/vl|vision|llava|gemma3|minicpm-v|moondream/i.test(m)) ?? models[0] ?? null;
    const g = ais.guide ?? {};
    b.innerHTML = `<h3 style="margin-top:0">${esc(vision ? t("Model for the frame analysis (optional)") : t("Which model answers?"))}</h3>
      <p class="muted small">${esc(vision ? t("Looks at a few frames of a file and proposes filters and upscaling. It must be a vision model; empty = the text model.") : t("A 7B instruction model is a good balance; 3B is faster on weak computers."))}</p>
      <input id="ais-model" list="ais-models" value="${esc(a[key] ?? "")}"><datalist id="ais-models">${models.map(m => `<option value="${esc(m)}">`).join("")}</datalist>
      ${models.length ? "" : `<p class="issue warn">${esc(t("No suitable model on the server. Download one ({0}), then go back and check again.", vision ? g.suggestedVision ?? "" : g.suggestedText ?? ""))}</p>`}
      <div class="row" style="margin-top:8px"><button class="btn sm" type="button" id="ais-try">${esc(t("Try it"))}</button></div><div id="ais-result"></div>`;
    b.oninput = e => { if (e.target.id === "ais-model") a[key] = e.target.value.trim() || null; };
    $("#ais-try").onclick = guard(async () => {
      const m = vision ? a.visionModel ?? a.model : a.model;
      if (!m) return;
      $("#ais-result").innerHTML = `<p class="muted small">${esc(t("Asking {0}… (the first answer can take a minute while the model loads)", m))}</p>`;
      const r = await post("/api/ai/setup/test", { ai: aiBody(), model: m });
      $("#ais-result").innerHTML = r.error ? `<p class="issue err">✖ ${esc(r.error)}</p>` : `<p class="issue info">✔ ${esc(t("{0} answers correctly.", m))}</p>`;
    });
    return;
  }
  const prov = state.options.llmProviders.find(p => p.value === a.provider)?.label ?? a.provider;
  b.innerHTML = `<h3 style="margin-top:0">${esc(t("Ready"))}</h3>
    <div class="section"><ul class="wiz-summary"><li>${esc(t("Server"))}: ${esc(prov)}${a.baseUrl ? " · " + esc(a.baseUrl) : ""}</li>
      <li>${esc(t("Model"))}: ${esc(a.model ?? "—")}</li><li>${esc(t("Vision model"))}: ${esc(a.visionModel ?? a.model ?? "—")}</li></ul></div>
    <p class="muted small">${esc(t("Then: the AI assistant button of the encode settings panel turns a request in plain words into settings; in the editor (Filters tab) \"Analyse frames with AI\" proposes filters for a file. Nothing changes until you apply it."))}</p>`;
}

$("#ais-close").onclick = () => $("#dlg-ai-setup").close();
$("#ais-back").onclick = () => { ais.step--; renderAiSetup(); };
$("#ais-next").onclick = guard(async () => {
  if (ais.step < AIS_STEPS - 1) { ais.step++; return renderAiSetup(); }
  const r = await api("PUT", "/api/settings", { ai: aiBody() });
  state.settings = r.settings;
  $("#dlg-ai-setup").close();
  toast(t("Saved"), "ok");
  ais.done?.();
});
