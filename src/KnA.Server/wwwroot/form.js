// Settings form shared by the side panel ("quick") and the editor Output tab ("full").
import { $, $$, esc, state, t, label, profileOf, fillSelect, capability } from "./core.js";

function backendStatus(codec, backend, tenBit) {
  const c = capability(codec, backend);
  if (!state.settings.capabilities) return { ok: true, text: "" };
  if (!c) return { ok: false, text: t("not available for this codec") };
  if (tenBit ? c.works10Bit : c.works8Bit) return { ok: true, text: "" };
  const rig = state.options.rigayaEncoders?.[`${codec}|${backend}`];
  if (rig && state.tools?.rigaya?.includes(backend)) return { ok: true, text: t("only with {0}", rig.name) };
  if (!c.compiled) return { ok: false, text: t("not in this FFmpeg build") };
  return { ok: false, text: tenBit && c.works8Bit ? t("8-bit only") : t("no compatible GPU/driver") };
}

/**
 * Renders the form into `root` for the settings object `s` (mutated in place).
 * opts: { full: bool, allowedContainers: [], onChange(s) }
 */
export function renderSettingsForm(root, s, opts = {}) {
  const o = state.options, p = profileOf(s.profile);
  const full = !!opts.full;
  const codec = p.value === "RemuxMp4" ? "H264" : s.codec;
  const tenBitPossible = o.tenBitCodecs.includes(codec) && ["UniversalMkv", "Streaming"].includes(p.value);
  const pro = p.value === "Professional";
  if (!p.codecs.includes(s.codec) && p.codecs.length) s.codec = p.codecs[0];
  if (!p.allowsHardware) s.backend = "Software";
  const backends = o.backends.map(b => {
    const st = backendStatus(codec, b.value, s.tenBit && tenBitPossible);
    return { value: b.value, label: b.label + (st.text ? " — " + st.text : ""), disabled: !st.ok };
  });
  if (backends.find(b => b.value === s.backend)?.disabled) s.backend = backends.find(b => !b.disabled)?.value ?? "Software";

  const show = {
    codec: p.codecs.length > 1,
    codecSelect: p.codecs.length > 4,
    encoder: p.encodesVideo || p.value === "RemuxMp4",
    tenBit: p.encodesVideo,
    res: p.usesResolution, quality: p.usesQuality, subs: p.usesSubtitleMode,
    hdr: p.usesVideoFilters, images: p.isImages, demux: p.isDemux,
    container: full && (opts.allowedContainers?.length ?? 0) > 1,
  };
  root.innerHTML = `
    <label class="field"><span>${esc(t("Profile"))}</span><select data-k="profile"></select>
      <small class="muted">${esc(p.description)}</small></label>
    ${show.codec && show.codecSelect ? `<label class="field"><span>${esc(t("Video codec"))}</span><select data-k="codec"></select></label>` : ""}
    ${show.codec && !show.codecSelect ? `<div class="field"><span>${esc(t("Video codec"))}</span><div class="segmented" data-k="codec">
      ${p.codecs.map(c => `<button type="button" data-v="${c}" class="${c === s.codec ? "on" : ""}">${esc(label(o.codecs, c).split(" /")[0])}</button>`).join("")}
    </div></div>` : ""}
    ${show.encoder ? `<label class="field"><span>${esc(t("Encoder"))}</span><select data-k="backend" ${p.allowsHardware ? "" : "disabled"}></select>
      <small class="muted">${esc(!p.allowsHardware ? t("This profile is CPU only.") : s.backend === "Software" ? t("CPU encoding: slowest, best compression.") : t("Hardware encoding: much faster, slightly bigger files."))}</small></label>` : ""}
    ${show.tenBit && !pro ? `<label class="toggle ${tenBitPossible ? "" : "disabled"}"><input type="checkbox" data-k="tenBit" ${s.tenBit && tenBitPossible ? "checked" : ""} ${tenBitPossible ? "" : "disabled"}>
      <span class="switch"></span><span>${esc(t("10-bit output"))} <small class="muted">(HEVC / AV1 / VP9 / VVC)</small></span></label>` : ""}
    ${pro ? `<div class="grid2">
      ${s.codec === "ProRes" ? `<label class="field"><span>ProRes</span><select data-k="pro.proRes"></select></label>` : ""}
      ${s.codec === "DNxHR" ? `<label class="field"><span>DNxHR</span><select data-k="pro.dnxhr"></select></label>` : ""}
      <label class="field"><span>${esc(t("Chroma"))}</span><select data-k="pro.chroma"></select></label>
      ${["FFV1", "FFVHuff", "JPEG2000"].includes(s.codec) ? `<label class="field"><span>${esc(t("Bit depth"))}</span><select data-k="pro.bitDepth"></select></label>` : ""}
    </div>` : ""}
    ${(show.res || show.quality) ? `<div class="grid2">
      ${show.res ? `<label class="field"><span>${esc(t("Resolution"))}</span><select data-k="resolution"></select></label>` : "<div></div>"}
      ${show.quality ? `<label class="field"><span>${esc(t("Quality"))}</span><select data-k="quality"></select></label>` : ""}
    </div>` : ""}
    ${show.subs ? `<label class="field"><span>${esc(t("Subtitles"))}</span><select data-k="subtitles"></select>
      <small class="muted">${esc(t("Automatic plan: the editor (Tracks tab) can override it per file."))}</small></label>
      ${s.subtitles === "Hardsub" ? `<label class="toggle"><input type="checkbox" data-k="useSidecarSubtitles" ${s.useSidecarSubtitles ? "checked" : ""}><span class="switch"></span><span>${esc(t("Prefer external .ass/.srt next to the video"))}</span></label>` : ""}` : ""}
    ${show.container ? `<label class="field"><span>${esc(t("Container"))}</span><select data-k="container"></select></label>` : ""}
    ${p.usesAudio ? `<div class="grid2">
      <label class="field"><span>${esc(t("Audio"))}</span><select data-k="audioCodec"></select></label>
      ${!["Auto", "Copy", "Flac", "Alac", "WavPack", "TrueHd", "Pcm16", "Pcm24", "Pcm32Float"].includes(s.audioCodec ?? "Auto") || (s.audioCodec ?? "Auto") === "Auto"
        ? `<label class="field"><span>${esc(t("Bitrate (kb/s)"))}</span><input type="number" min="8" max="1536" step="8" data-k="audioBitrate" value="${s.audioBitrate ?? ""}" placeholder="${esc(t("auto"))}"></label>` : "<div></div>"}
    </div>${["Auto", "Aac", "HeAac"].includes(s.audioCodec ?? "Auto") ? `<small class="muted">${esc(aacNote())}</small>` : ""}` : ""}
    ${p.usesVideoFilters && p.usesResolution ? `<details class="upscale" ${s.upscale?.method && s.upscale.method !== "Standard" ? "open" : ""}><summary>${esc(t("Upscaling / AI"))}</summary>
      <label class="field"><span>${esc(t("Method"))}</span><select data-k="upscale.method"></select>
        <small class="muted">${esc(o.upscaleMethods.find(m => m.value === (s.upscale?.method ?? "Standard"))?.description ?? "")}</small></label>
      ${s.upscale?.method === "Anime4K" ? `<label class="field"><span>Anime4K</span><select data-k="upscale.model"></select></label>` : ""}
      ${s.upscale?.method === "Neural" ? `<label class="field"><span>${esc(t("Model"))}</span><select data-k="upscale.model"></select>
        <small class="muted">${esc(o.neuralModels.find(m => m.value === (s.upscale?.model ?? o.neuralModels[0].value))?.description ?? "")}</small></label>` : ""}
      ${s.upscale?.method && s.upscale.method !== "Standard" ? `<label class="toggle"><input type="checkbox" data-k="upscale.alwaysApply" ${s.upscale.alwaysApply ? "checked" : ""}><span class="switch"></span><span>${esc(t("Also at the same size (restoration only)"))}</span></label>
        <small class="muted">${esc(t("Used when the chosen resolution is above the source."))}</small>` : ""}
    </details>` : ""}
    ${full && show.hdr ? `<label class="field"><span>HDR</span><select data-k="hdr"></select></label>
      ${s.hdr !== "Preserve" ? `<label class="field"><span>${esc(t("HDR → SDR tone mapping"))}</span><select data-k="toneMap"></select></label>
      <label class="toggle"><input type="checkbox" data-k="toneMapDynamic" ${s.toneMapDynamic ? "checked" : ""}><span class="switch"></span><span>${esc(t("Adapt brightness scene by scene (dynamic peak)"))}</span></label>
      <small class="muted">${esc(t("Used only when an HDR source becomes SDR. Off: the brightness follows the mastering metadata and stays stable across the film; on, dim scenes are brightened."))}</small>` : ""}` : ""}
    ${p.encodesVideo ? `<label class="toggle"><input type="checkbox" data-k="chunks.enabled" ${s.chunks?.enabled ? "checked" : ""}><span class="switch"></span>
        <span>${esc(t("Segmented encoding (resumable, parallel)"))}</span></label>
      ${s.chunks?.enabled ? `<div class="grid2">
        <label class="field"><span>${esc(t("Parallel segments"))}</span><input type="number" min="1" max="32" data-k="chunks.workers" value="${s.chunks.workers}"></label>
        <label class="field"><span>${esc(t("Segment length (s)"))}</span><input type="number" min="5" step="5" data-k="chunks.targetSeconds" value="${s.chunks.targetSeconds}"></label>
      </div><small class="muted">${esc(t("Splits at keyframes, encodes segments side by side and resumes from the last finished one after a stop. Best with CPU encoders."))}</small>` : ""}` : ""}
    ${show.images ? `<div class="grid2">
        <label class="field"><span>${esc(t("Image format"))}</span><select data-k="images.format"></select></label>
        <label class="field"><span>${esc(t("Which frames"))}</span><select data-k="images.mode"></select></label>
      </div>
      <div class="grid2">
        ${s.images.mode === "Interval" ? `<label class="field"><span>${esc(t("Every (seconds)"))}</span><input type="number" min="0.04" step="0.5" data-k="images.intervalSeconds" value="${s.images.intervalSeconds}"></label>` : ""}
        ${s.images.mode === "Count" ? `<label class="field"><span>${esc(t("Number of images"))}</span><input type="number" min="1" step="1" data-k="images.count" value="${s.images.count}"></label>` : ""}
        ${["Jpeg", "Webp"].includes(s.images.format) ? `<label class="field"><span>${esc(t("Quality"))} (1–100)</span><input type="number" min="1" max="100" data-k="images.quality" value="${s.images.quality}"></label>` : ""}
      </div>
      ${["Png", "Tiff"].includes(s.images.format) ? `<label class="toggle"><input type="checkbox" data-k="images.sixteenBit" ${s.images.sixteenBit ? "checked" : ""}><span class="switch"></span><span>${esc(t("16 bits per channel"))}</span></label>` : ""}` : ""}
    ${show.demux ? `
      <label class="toggle"><input type="checkbox" data-k="demux.raw" ${s.demux.raw ? "checked" : ""}><span class="switch"></span><span>${esc(t("Raw elementary streams (.h264, .aac…) instead of MKA/MKS wrappers"))}</span></label>
      <label class="toggle"><input type="checkbox" data-k="demux.attachments" ${s.demux.attachments ? "checked" : ""}><span class="switch"></span><span>${esc(t("Extract attachments (fonts)"))}</span></label>
      <label class="toggle"><input type="checkbox" data-k="demux.chapters" ${s.demux.chapters ? "checked" : ""}><span class="switch"></span><span>${esc(t("Extract chapters"))}</span></label>` : ""}
    ${full && p.encodesVideo ? advancedEncoder(s, codec) : ""}
    ${full && (p.encodesVideo || p.isRemux || p.isImages || p.isDemux) ? trackSelection(s) : ""}
  `;
  const sel = k => $(`[data-k="${k}"]`, root);
  fillSelect(sel("profile"), o.profiles, s.profile);
  if (show.encoder) fillSelect(sel("backend"), backends, s.backend);
  if (show.res) fillSelect(sel("resolution"), o.resolutions, s.resolution);
  if (show.quality) fillSelect(sel("quality"), o.qualities, s.quality);
  if (show.subs) fillSelect(sel("subtitles"), o.subtitleModes, s.subtitles);
  if (show.container) fillSelect(sel("container"), o.containers.filter(c => opts.allowedContainers.includes(c.value)), s.container ?? p.container);
  if (show.codec && show.codecSelect) fillSelect(sel("codec"), p.codecs.map(c => ({ value: c, label: label(o.codecs, c) })), s.codec);
  if (pro) {
    s.pro ??= { proRes: "Auto", dnxhr: "Auto", chroma: "Auto", bitDepth: 0 };
    if (sel("pro.proRes")) fillSelect(sel("pro.proRes"), o.proResProfiles, s.pro.proRes);
    if (sel("pro.dnxhr")) fillSelect(sel("pro.dnxhr"), o.dnxhrProfiles, s.pro.dnxhr);
    fillSelect(sel("pro.chroma"), o.chromaFormats, s.pro.chroma);
    if (sel("pro.bitDepth")) fillSelect(sel("pro.bitDepth"), [{ value: "0", label: t("Same as the source") }, ...[8, 10, 12, 16].map(d => ({ value: String(d), label: d + "-bit" }))], s.pro.bitDepth);
  }
  if (p.usesAudio) fillSelect(sel("audioCodec"), o.audioCodecs, s.audioCodec ?? "Auto");
  if (sel("upscale.method")) {
    s.upscale ??= { method: "Standard", model: null, alwaysApply: false };
    fillSelect(sel("upscale.method"), o.upscaleMethods.map(m => ({ ...m, label: m.label + upscaleNote(m.value) })), s.upscale.method);
    if (s.upscale.method === "Anime4K") fillSelect(sel("upscale.model"), o.anime4KModes.map(m => ({ value: m.value, label: `${m.label} — ${m.description}` })), s.upscale.model ?? "A");
    if (s.upscale.method === "Neural") fillSelect(sel("upscale.model"), o.neuralModels, s.upscale.model ?? o.neuralModels[0].value);
  }
  if (full && show.hdr) fillSelect(sel("hdr"), o.hdrModes, s.hdr);
  if (sel("toneMap")) fillSelect(sel("toneMap"), o.toneMapCurves, s.toneMap ?? "Spline");
  if (show.images) { fillSelect(sel("images.format"), o.imageFormats, s.images.format); fillSelect(sel("images.mode"), o.imageModes, s.images.mode); }

  const changed = () => { renderSettingsForm(root, s, opts); opts.onChange?.(s); };
  root.onchange = e => {
    const k = e.target.dataset.k; if (!k) return;
    let v = e.target.type === "checkbox" ? e.target.checked : e.target.value;
    if (["resolution"].includes(k)) v = Number(v);
    if (["images.intervalSeconds", "images.count", "images.quality", "chunks.workers", "chunks.targetSeconds", "pro.bitDepth"].includes(k)) v = Number(v);
    if (k === "audioBitrate") v = v === "" ? null : Number(v);
    if (["encoder.qualityValue", "encoder.keyframeSeconds"].includes(k)) v = v === "" || Number(v) <= 0 && k !== "encoder.qualityValue" ? null : Number(v);
    if (["encoder.bitrateKbps", "encoder.maxBitrateKbps", "encoder.bFrames", "encoder.lookahead"].includes(k)) v = v === "" || Number(v) < 0 || (k.includes("itrate") && Number(v) === 0) ? null : Math.round(Number(v));
    if (["encoder.speed", "encoder.tune", "encoder.extra", "trackRules.audioLanguages", "trackRules.subtitleLanguages"].includes(k)) v = String(v).trim() || null;

    if (k === "upscale.method") s.upscale = { ...s.upscale, model: null };
    if (k === "encoder.program") s.encoder = { ...s.encoder, speed: null, tune: null, extra: null };
    const [a, b] = k.split(".");
    if (b) s[a] = { ...s[a], [b]: v }; else s[a] = v;
    if (k === "profile") { s.container = null; }
    changed();
  };
  root.onclick = e => {
    if (e.target.closest("[data-reset=encoder]")) { s.encoder = {}; changed(); return; }
    const b = e.target.closest("[data-k=codec] button");
    if (b) { s.codec = b.dataset.v; changed(); }
  };
}

/** Hand-set encoder parameters (full form only); empty = KnA's values for the quality level. */
function advancedEncoder(s, codec) {
  const o = state.options, e = s.encoder ?? {};
  const ffEnc = o.encoderNames?.[`${codec}|${s.backend ?? "Software"}`] ?? "";
  const rig = o.rigayaEncoders?.[`${codec}|${s.backend ?? "Software"}`];
  const useRig = !!rig && e.program === "Rigaya";
  const enc = useRig ? rig.name : ffEnc;
  const info = (useRig ? rig.info : o.encoders?.[ffEnc]) ?? { speeds: [], tunes: [] };
  const rc = e.rateControl ?? "Auto";
  const opt = (v, l, cur) => `<option value="${esc(v)}" ${String(cur ?? "") === v ? "selected" : ""}>${esc(l)}</option>`;
  const empty = (e.program ?? "FFmpeg") === "FFmpeg" && rc === "Auto" && !e.speed && !e.tune && e.keyframeSeconds == null && e.bFrames == null && e.lookahead == null && !e.extra;
  const installed = !rig || (state.tools?.rigaya ?? []).includes(s.backend);
  return `<details class="upscale" ${empty ? "" : "open"}><summary>${esc(t("Advanced encoder"))}${empty ? "" : " ●"}</summary>
    ${rig ? `<label class="field"><span>${esc(t("Encoder program"))}</span><select data-k="encoder.program">
      ${opt("FFmpeg", t("FFmpeg ({0})", ffEnc), e.program ?? "FFmpeg")}${opt("Rigaya", rig.name + " (rigaya)", e.program)}</select>
      <small class="muted">${esc(t("{0} reads the source with GPU decoding, resizes on the GPU and copies Dolby Vision / HDR10+ when no FFmpeg filter is needed (otherwise FFmpeg decodes and filters, and sends the frames to it). Install it in Settings → Components.", rig.name))}</small>
      ${useRig && !installed ? `<small class="warn">${esc(t("{0} is not installed: add it in Settings → Components, or choose FFmpeg as the encoder program.", rig.name))}</small>` : ""}</label>` : ""}
    <small class="muted">${esc(t("Encoder: {0}. Empty fields keep KnA's values for the chosen quality.", enc))}</small>
    <label class="field"><span>${esc(t("Rate control"))}</span><select data-k="encoder.rateControl">
      ${opt("Auto", t("From the quality level"), rc)}${info.qualityLabel ? opt("Quality", t("Constant quality ({0})", info.qualityLabel), rc) : ""}${opt("Bitrate", t("Average bitrate"), rc)}</select></label>
    ${rc === "Quality" && info.qualityLabel ? `<label class="field"><span>${esc(t("{0} value ({1}–{2})", info.qualityLabel, info.qualityMin, info.qualityMax))}</span>
      <input type="number" step="0.5" min="${info.qualityMin}" max="${info.qualityMax}" data-k="encoder.qualityValue" value="${e.qualityValue ?? ""}"></label>` : ""}
    ${rc === "Bitrate" ? `<div class="grid2"><label class="field"><span>${esc(t("Bitrate (kb/s)"))}</span><input type="number" min="100" step="100" data-k="encoder.bitrateKbps" value="${e.bitrateKbps ?? 6000}"></label>
      <label class="field"><span>${esc(t("Maximum (kb/s, 0 = none)"))}</span><input type="number" min="0" step="100" data-k="encoder.maxBitrateKbps" value="${e.maxBitrateKbps ?? 0}"></label></div>` : ""}
    <div class="grid2">
      <label class="field"><span>${esc(t("Speed preset (fastest → slowest)"))}</span><select data-k="encoder.speed">${opt("", t("From the quality level"), e.speed)}${info.speeds.map(v => opt(v, v, e.speed)).join("")}</select></label>
      <label class="field"><span>${esc(t("Tune"))}</span><select data-k="encoder.tune">${opt("", t("None"), e.tune)}${info.tunes.map(v => opt(v, v, e.tune)).join("")}</select></label>
    </div>
    <div class="grid3">
      <label class="field"><span>${esc(t("Keyframe every (s, 0 = auto)"))}</span><input type="number" min="0" step="0.5" data-k="encoder.keyframeSeconds" value="${e.keyframeSeconds ?? 0}"></label>
      <label class="field"><span>${esc(t("B-frames (−1 = auto)"))}</span><input type="number" min="-1" max="16" data-k="encoder.bFrames" value="${e.bFrames ?? -1}"></label>
      <label class="field"><span>${esc(t("Lookahead (frames, −1 = auto)"))}</span><input type="number" min="-1" max="250" data-k="encoder.lookahead" value="${e.lookahead ?? -1}"></label>
    </div>
    <label class="field"><span>${esc(t("Extra encoder options"))}</span><input class="mono" data-k="encoder.extra" value="${esc(e.extra ?? "")}" placeholder="${useRig ? "--aq-temporal --bref-mode each" : "-x265-params psy-rd=2:aq-strength=0.8"}">
      <small class="muted">${esc(useRig ? t("{0} options (see its documentation), applied last.", rig.name) : t("FFmpeg syntax, applied last (they replace KnA's values)."))}</small></label>
    ${empty ? "" : `<button type="button" class="btn sm ghost" data-reset="encoder">${esc(t("Back to automatic"))}</button>`}
  </details>`;
}

/** Track selection rules kept in presets. */
function trackSelection(s) {
  const r = s.trackRules ?? {};
  const empty = !r.audioLanguages && !r.subtitleLanguages && !r.dropCommentary && !r.dropAttachments;
  return `<details class="upscale" ${empty ? "" : "open"}><summary>${esc(t("Track selection"))}${empty ? "" : " ●"}</summary>
    <small class="muted">${esc(t("Applied to the automatic track plan of every file: the tracks left out can still be ticked by hand in the editor."))}</small>
    <div class="grid2">
      <label class="field"><span>${esc(t("Audio languages to keep"))}</span><input data-k="trackRules.audioLanguages" value="${esc(r.audioLanguages ?? "")}" placeholder="ita, jpn"><small class="muted">${esc(t("In order of preference; empty = all."))}</small></label>
      <label class="field"><span>${esc(t("Subtitle languages to keep"))}</span><input data-k="trackRules.subtitleLanguages" value="${esc(r.subtitleLanguages ?? "")}" placeholder="ita"><small class="muted">${esc(t("Empty = all."))}</small></label>
    </div>
    <label class="toggle"><input type="checkbox" data-k="trackRules.dropCommentary" ${r.dropCommentary ? "checked" : ""}><span class="switch"></span><span>${esc(t("Leave out commentary tracks"))}</span></label>
    <label class="toggle"><input type="checkbox" data-k="trackRules.dropAttachments" ${r.dropAttachments ? "checked" : ""}><span class="switch"></span><span>${esc(t("Leave out the attachments of the file"))}</span></label>
  </details>`;
}

/** Which AAC encoder the server will use (best first). */
function aacNote() {
  const builds = state.settings.capabilities?.builds ?? [];
  if (builds.some(b => b.encoders.includes("libfdk_aac"))) return t("AAC: libfdk_aac (Fraunhofer, best quality).");
  if (builds.some(b => b.encoders.includes("aac_at"))) return t("AAC: Apple AudioToolbox (excellent quality).");
  if (state.tools?.qaac) return t("AAC: qaac (Apple Core Audio, best quality); libfdk_aac / fdkaac as fallback.");
  if (state.tools?.fdkaac) return t("AAC: fdkaac program (Fraunhofer, best quality).");
  return t("AAC: FFmpeg's own encoder. Install fdkaac in Settings → Components for better quality.");
}

function upscaleNote(method) {
  const caps = state.settings.capabilities;
  if (["Placebo", "Anime4K", "Fsrcnnx"].includes(method) && caps && !caps.hasLibplacebo) return " — " + t("needs libplacebo");
  return "";
}

export function cloneSettings(s) { return JSON.parse(JSON.stringify(s)); }
