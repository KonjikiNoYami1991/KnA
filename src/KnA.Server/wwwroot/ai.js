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
