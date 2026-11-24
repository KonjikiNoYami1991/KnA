// Settings form shared by the side panel ("quick") and the editor tabs ("full": one section per tab — video, audio,
// subtitles, output — so every setting appears in exactly one place).
import { $, $$, esc, state, t, label, profileOf, fillSelect, capability, api } from "./core.js";

function backendStatus(codec, backend, tenBit) {
  const c = capability(codec, backend);
  const caps = state.settings.capabilities;
  // A GPU encoder only when the server's hardware detection found that GPU (a test encode worked on it).
  if (!caps) return backend === "Software" ? { ok: true, text: "" } : { ok: false, text: t("hardware not detected yet") };
  const gpuPresent = backend === "Software" || (caps.encoders ?? []).some(e => e.backend === backend && (e.works8Bit || e.works10Bit));
  if (!c) return { ok: false, text: t("not available for this codec") };
  if (tenBit ? c.works10Bit : c.works8Bit) return { ok: true, text: "" };
  const rig = state.options.rigayaEncoders?.[`${codec}|${backend}`];
  // Not for AV1: a GPU whose AV1 test failed has no AV1 encoder (only Ada/Blackwell, Arc, RDNA 3 and newer have one).
  if (rig && gpuPresent && codec !== "AV1" && state.tools?.rigaya?.includes(backend)) return { ok: true, text: t("only with {0}", rig.name) };
  if (!gpuPresent) return { ok: false, text: t("no such GPU in this computer") };
  if (!c.compiled) return { ok: false, text: t("not in this FFmpeg build") };
  return { ok: false, text: tenBit && c.works8Bit ? t("8-bit only") : t("no compatible GPU/driver") };
}

/**
 * Renders the form into `root` for the settings object `s` (mutated in place).
 * opts: { full: bool, section: "video"|"audio"|"subtitles"|"output", allowedContainers: [], onChange(s), sizeInfo(s) → Promise<text> }
 */
export function renderSettingsForm(root, s, opts = {}) {
  const o = state.options, p = profileOf(s.profile);
  const full = !!opts.full;
  const sec = full ? opts.section ?? null : null;
  const In = x => !sec || sec === x;
  s.encoder ??= {}; s.arguments ??= {};
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

  const V = In("video");
  if (sec === "videoRate") { root.innerHTML = p.encodesVideo && p.usesQuality && !p.isImages ? editorRate(s, codec) : `<p class="muted small">${esc(t("This profile does not encode the video."))}</p>`; bindForm(root, s, opts, codec); return; }
  if (sec === "presetRules") { root.innerHTML = presetRules(s, p); bindForm(root, s, opts, codec); return; }
  const rcHand = (s.encoder?.rateControl ?? "Auto") !== "Auto";
  const show = {
    profile: V,
    codec: V && p.codecs.length > 1,
    codecSelect: p.codecs.length > 4,
    encoder: V && (p.encodesVideo || p.value === "RemuxMp4"),
    tenBit: V && p.encodesVideo,
    res: V && p.usesResolution,
    // one place decides the quality: the level in the quick panel (unless set by hand), the rate control in the editor
    quality: V && p.usesQuality && !full && !rcHand,
    qualityHand: V && p.usesQuality && !full && rcHand,
    rate: V && full && !sec && p.encodesVideo && p.usesQuality,
    subs: In("subtitles") && p.usesSubtitleMode,
    hdr: V && p.usesVideoFilters, images: V && p.isImages, demux: In("output") && p.isDemux,
    container: In("output") && full && (opts.allowedContainers?.length ?? 0) > 1,
    audio: In("audio") && p.usesAudio,
    upscale: V && p.usesVideoFilters && p.usesResolution,
    chunks: V && p.encodesVideo,
  };
  root.innerHTML = `
    ${show.profile ? `<label class="field"><span>${esc(t("Profile"))}</span><select data-k="profile"></select>
      <small class="muted">${esc(p.description)}</small></label>` : ""}
    ${show.codec && show.codecSelect ? `<label class="field"><span>${esc(t("Video codec"))}</span><select data-k="codec"></select></label>` : ""}
    ${show.codec && !show.codecSelect ? `<div class="field"><span>${esc(t("Video codec"))}</span><div class="segmented" data-k="codec">
      ${p.codecs.map(c => `<button type="button" data-v="${c}" class="${c === s.codec ? "on" : ""}">${esc(label(o.codecs, c).split(" /")[0])}</button>`).join("")}
    </div></div>` : ""}
    ${show.encoder ? `<label class="field"><span>${esc(t("Encoder"))}</span><select data-k="backend" ${p.allowsHardware ? "" : "disabled"}></select>
      <small class="muted">${esc(!p.allowsHardware ? t("This profile is CPU only.") : s.backend === "Software" ? t("CPU encoding: slowest, best compression.") : t("Hardware encoding: much faster, slightly bigger files."))}</small></label>` : ""}
    ${show.tenBit && !pro ? `<label class="toggle ${tenBitPossible ? "" : "disabled"}"><input type="checkbox" data-k="tenBit" ${s.tenBit && tenBitPossible ? "checked" : ""} ${tenBitPossible ? "" : "disabled"}>
      <span class="switch"></span><span>${esc(t("10-bit output"))} <small class="muted">(HEVC / AV1 / VP9 / VVC)</small></span></label>` : ""}
    ${V && pro ? `<div class="grid2">
      ${s.codec === "ProRes" ? `<label class="field"><span>ProRes</span><select data-k="pro.proRes"></select></label>` : ""}
      ${s.codec === "DNxHR" ? `<label class="field"><span>DNxHR</span><select data-k="pro.dnxhr"></select></label>` : ""}
      <label class="field"><span>${esc(t("Chroma"))}</span><select data-k="pro.chroma"></select></label>
      ${["FFV1", "FFVHuff", "JPEG2000"].includes(s.codec) ? `<label class="field"><span>${esc(t("Bit depth"))}</span><select data-k="pro.bitDepth"></select></label>` : ""}
    </div>` : ""}
    ${(show.res || show.quality || show.qualityHand) ? `<div class="grid2">
      ${show.res ? `<label class="field"><span>${esc(t("Resolution"))}</span><select data-k="resolution"></select></label>` : "<div></div>"}
      ${show.quality ? `<label class="field"><span>${esc(t("Quality"))}</span><select data-k="quality"></select></label>` : ""}
      ${show.qualityHand ? `<div class="field"><span>${esc(t("Quality"))}</span><small class="muted">${esc(rateText(s.encoder))}</small></div>` : ""}
    </div>` : ""}
    ${show.rate ? rateControl(s, codec) : ""}
    ${show.subs ? `<label class="field"><span>${esc(t("Subtitles"))}</span><select data-k="subtitles"></select>
      <small class="muted">${esc(t("Automatic plan: the editor can override it per file."))}</small></label>
      ${s.subtitles === "Hardsub" ? `<label class="toggle"><input type="checkbox" data-k="useSidecarSubtitles" ${s.useSidecarSubtitles ? "checked" : ""}><span class="switch"></span><span>${esc(t("Prefer external .ass/.srt next to the video"))}</span></label>` : ""}
      ${s.subtitles === "Softsub" || full ? `<label class="toggle"><input type="checkbox" data-k="ocrPictureSubtitles" ${s.ocrPictureSubtitles ? "checked" : ""}><span class="switch"></span><span>${esc(t("Convert picture subtitles (PGS, DVD) to text with OCR"))}</span></label>
        ${s.ocrPictureSubtitles ? `<small class="muted">${esc(t("Tesseract with the language model of each track, like Subtitle Edit: lighter files, and subtitles MP4 and players without PGS support can show. Settings → Components installs it."))}</small>` : ""}` : ""}` : ""}
    ${show.container ? `<label class="field"><span>${esc(t("Container"))}</span><select data-k="container"></select></label>` : ""}
    ${show.audio ? `<div class="grid2">
      <label class="field"><span>${esc(t("Audio"))}</span><select data-k="audioCodec"></select></label>
      ${!["Auto", "Copy", "Flac", "Alac", "WavPack", "TrueHd", "Pcm16", "Pcm24", "Pcm32Float"].includes(s.audioCodec ?? "Auto") || (s.audioCodec ?? "Auto") === "Auto"
        ? `<label class="field"><span>${esc(t("Bitrate (kb/s)"))}</span><input type="number" min="8" max="1536" step="8" data-k="audioBitrate" value="${s.audioBitrate ?? ""}" placeholder="${esc(t("auto"))}"></label>` : "<div></div>"}
    </div>${["Auto", "Aac", "HeAac"].includes(s.audioCodec ?? "Auto") ? `<small class="muted">${esc(aacNote())}</small>` : ""}` : ""}
    ${show.upscale ? `<details class="upscale" ${s.upscale?.method && s.upscale.method !== "Standard" ? "open" : ""}><summary>${esc(t("Upscaling / AI"))}</summary>
      <label class="field"><span>${esc(t("Method"))}</span><select data-k="upscale.method"></select>
        <small class="muted">${esc(o.upscaleMethods.find(m => m.value === (s.upscale?.method ?? "Standard"))?.description ?? "")}</small></label>
      ${s.upscale?.method === "Anime4K" ? `<label class="field"><span>Anime4K</span><select data-k="upscale.model"></select></label>` : ""}
      ${s.upscale?.method === "Neural" && state.tools && !state.tools.aiRuntime ? `<div class="field"><button type="button" class="btn primary sm" data-install="ai-runtime">${esc(t("Install the AI upscaler (Python + PyTorch, several GB)"))}</button>
        <small class="muted">${esc(t("Needed once for the neural models: KnA picks the PyTorch build for your GPU (CUDA, ROCm, Intel, Apple or CPU)."))}</small></div>` : ""}
      ${s.upscale?.method === "Neural" ? `<label class="field"><span>${esc(t("Model"))}</span><select data-k="upscale.model"></select>
        <small class="muted">${esc(o.neuralModels.find(m => m.value === (s.upscale?.model ?? o.neuralModels[0].value))?.description ?? "")}</small></label>` : ""}
      ${s.upscale?.method && s.upscale.method !== "Standard" ? `<label class="toggle"><input type="checkbox" data-k="upscale.alwaysApply" ${s.upscale.alwaysApply ? "checked" : ""}><span class="switch"></span><span>${esc(t("Also at the same size (restoration only)"))}</span></label>
        <small class="muted">${esc(t("Used when the chosen resolution is above the source."))}</small>` : ""}
    </details>` : ""}
    ${full && show.hdr ? `<label class="field"><span>HDR</span><select data-k="hdr"></select></label>
      ${s.hdr !== "Preserve" ? `<label class="field"><span>${esc(t("HDR → SDR tone mapping"))}</span><select data-k="toneMap"></select></label>
      <label class="toggle"><input type="checkbox" data-k="toneMapDynamic" ${s.toneMapDynamic ? "checked" : ""}><span class="switch"></span><span>${esc(t("Adapt brightness scene by scene (dynamic peak)"))}</span></label>
      <small class="muted">${esc(t("Used only when an HDR source becomes SDR. Off: the brightness follows the mastering metadata and stays stable across the film; on, dim scenes are brightened."))}</small>` : ""}` : ""}
    ${show.chunks ? `<label class="toggle"><input type="checkbox" data-k="chunks.enabled" ${s.chunks?.enabled ? "checked" : ""}><span class="switch"></span>
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
    ${V && full && !sec && p.encodesVideo ? advancedEncoder(s, codec) : ""}
    ${V && full && sec === "video" && p.encodesVideo ? encoderProgram(s, codec) : ""}
    ${In("audio") && full && !sec && p.usesAudio ? `
      ${paramsBlock(t("FFmpeg audio encoder parameters"), "audio", "arguments.audioFfmpeg", s.arguments.audioFfmpeg, "-aac_coder twoloop")}
      ${state.tools?.fdkaac ? paramsBlock(t("fdkaac parameters"), "fdkaac", "arguments.fdkaac", s.arguments.fdkaac, "--afterburner 1") : ""}
      ${state.tools?.qaac ? paramsBlock(t("qaac parameters"), "qaac", "arguments.qaac", s.arguments.qaac, "--quality 2") : ""}` : ""}
    ${In("subtitles") && full && !sec && p.usesTrackPlan ? paramsBlock(t("Subtitle encoder parameters"), "subtitles", "arguments.subtitles", s.arguments.subtitles, "-alignment 1") : ""}
    ${In("output") && full && !sec && (p.encodesVideo || p.isRemux || p.isImages || p.isDemux) ? trackSelection(s) : ""}
    ${In("output") && full && !p.isImages ? paramsBlock(t("Container parameters"), "muxer", "arguments.muxer", s.arguments.muxer, "-reserve_index_space 50k") : ""}
  `;
  const sel = k => $(`[data-k="${k}"]`, root);
  if (show.profile) fillSelect(sel("profile"), o.profiles, s.profile);
  if (show.encoder) fillSelect(sel("backend"), backends, s.backend);
  if (show.res) fillSelect(sel("resolution"), o.resolutions, s.resolution);
  if (show.quality) fillSelect(sel("quality"), o.qualities, s.quality);
  if (show.subs) fillSelect(sel("subtitles"), o.subtitleModes, s.subtitles);
  if (show.container) fillSelect(sel("container"), o.containers.filter(c => opts.allowedContainers.includes(c.value)), s.container ?? p.container);
  if (show.codec && show.codecSelect) fillSelect(sel("codec"), p.codecs.map(c => ({ value: c, label: label(o.codecs, c) })), s.codec);
  if (V && pro) {
    s.pro ??= { proRes: "Auto", dnxhr: "Auto", chroma: "Auto", bitDepth: 0 };
    if (sel("pro.proRes")) fillSelect(sel("pro.proRes"), o.proResProfiles, s.pro.proRes);
    if (sel("pro.dnxhr")) fillSelect(sel("pro.dnxhr"), o.dnxhrProfiles, s.pro.dnxhr);
    fillSelect(sel("pro.chroma"), o.chromaFormats, s.pro.chroma);
    if (sel("pro.bitDepth")) fillSelect(sel("pro.bitDepth"), [{ value: "0", label: t("Same as the source") }, ...[8, 10, 12, 16].map(d => ({ value: String(d), label: d + "-bit" }))], s.pro.bitDepth);
  }
  if (show.audio) fillSelect(sel("audioCodec"), o.audioCodecs, s.audioCodec ?? "Auto");
  if (sel("upscale.method")) {
    s.upscale ??= { method: "Standard", model: null, alwaysApply: false };
    fillSelect(sel("upscale.method"), o.upscaleMethods.map(m => ({ ...m, label: m.label + upscaleNote(m.value) })), s.upscale.method);
    if (s.upscale.method === "Anime4K") fillSelect(sel("upscale.model"), o.anime4KModes.map(m => ({ value: m.value, label: `${m.label} — ${m.description}` })), s.upscale.model ?? "A");
    if (s.upscale.method === "Neural") fillSelect(sel("upscale.model"), o.neuralModels, s.upscale.model ?? o.neuralModels[0].value);
  }
  if (full && show.hdr) fillSelect(sel("hdr"), o.hdrModes, s.hdr);
  if (sel("toneMap")) fillSelect(sel("toneMap"), o.toneMapCurves, s.toneMap ?? "Spline");
  if (show.images) { fillSelect(sel("images.format"), o.imageFormats, s.images.format); fillSelect(sel("images.mode"), o.imageModes, s.images.mode); }

  bindForm(root, s, opts, codec);
}

/** Events of a rendered form (also the editor's rate control and preset-rules sections). */
function bindForm(root, s, opts, codec) {
  const changed = () => { renderSettingsForm(root, s, opts); opts.onChange?.(s); };
  // parameter rows save without redrawing the form (the row being typed in keeps its focus and stays open)
  mountParams(root, s, () => (opts.onQuietChange ?? opts.onChange)?.(s));
  // Optional GPU mini benchmark (editor only): expected time of this job with each rigaya profile.
  if ($("#gpu-estimate", root) && opts.gpuEstimate) {
    const box = $("#gpu-estimate", root), btn = $("[data-a=gpu-bench]", root);
    box.parentElement.hidden = false;
    opts.gpuEstimate(s).then(text => { if (box && text) box.textContent = text; }).catch(() => {});
    btn.onclick = async () => {
      box.textContent = t("Measuring…"); btn.disabled = true;
      try { await opts.gpuBenchmark(s); box.textContent = (await opts.gpuEstimate(s)) ?? ""; }
      catch (err) { box.textContent = err.message; }
      finally { btn.disabled = false; }
    };
  }
  if ($("#size-info", root) && s.encoder?.rateControl === "TargetSize" && opts.sizeInfo) {
    const box = $("#size-info", root);
    opts.sizeInfo(s).then(text => { if (box && text) box.textContent = text; }).catch(() => {});
  }
  root.onchange = e => {
    if (e.target.dataset.k === "levelFill") {
      if (!e.target.value) return;
      // a quality level only fills the fields with KnA's values for it: it is not kept as a rule
      s.quality = e.target.value; s.encoder = { ...s.encoder, rateControl: "Auto", qualityValue: null, speed: null };
      return opts.onLevel ? opts.onLevel(s) : changed();
    }
    if (e.target.dataset.k === "rigayaProfile") {
      const rig = state.options.rigayaEncoders?.[`${codec}|${s.backend ?? "Software"}`];
      const pick = rig?.profiles?.find(p => p.id === e.target.value);
      if (pick) { s.encoder = applyRigayaProfile(s.encoder ?? {}, pick); changed(); }
      return;
    }
    if (e.target.dataset.k === "encoder.rateControl" && e.target.value === "Quality" && s.encoder?.qualityValue == null && opts.onLevel) {
      s.encoder = { ...s.encoder, rateControl: "Auto", qualityValue: null };
      return opts.onLevel(s);
    }
    const k = e.target.dataset.k; if (!k) return;
    let v = e.target.type === "checkbox" ? e.target.checked : e.target.value;
    if (["resolution"].includes(k)) v = Number(v);
    if (["images.intervalSeconds", "images.count", "images.quality", "chunks.workers", "chunks.targetSeconds", "pro.bitDepth"].includes(k)) v = Number(v);
    if (k === "audioBitrate") v = v === "" ? null : Number(v);
    if (["encoder.qualityValue", "encoder.keyframeSeconds"].includes(k)) v = v === "" || Number(v) <= 0 && k !== "encoder.qualityValue" ? null : Number(v);
    if (k === "encoder.targetSizeMB") v = Number(v) > 0 ? Number(v) : null;
    if (["encoder.bitrateKbps", "encoder.maxBitrateKbps", "encoder.bFrames", "encoder.lookahead"].includes(k)) v = v === "" || Number(v) < 0 || (k.includes("itrate") && Number(v) === 0) ? null : Math.round(Number(v));
    if (["encoder.speed", "encoder.tune", "encoder.extra", "trackRules.audioLanguages", "trackRules.subtitleLanguages"].includes(k)) v = String(v).trim() || null;

    if (k === "upscale.method") s.upscale = { ...s.upscale, model: null };
    // the values shown as defaults are stored with the choice (a number on screen is never ignored)
    if (k === "encoder.rateControl") {
      if (v === "Bitrate") s.encoder = { ...s.encoder, bitrateKbps: s.encoder?.bitrateKbps ?? 6000 };
      if (v === "TargetSize") s.encoder = { ...s.encoder, targetSizeMB: s.encoder?.targetSizeMB ?? 4096 };
    }
    if (k === "encoder.program") s.encoder = { ...s.encoder, speed: null, tune: null, extra: null, gpuFilters: null };
    const [a, b] = k.split(".");
    if (b) s[a] = { ...s[a], [b]: v }; else s[a] = v;
    if (k === "profile") { s.container = null; }
    changed();
  };
  root.onclick = async e => {
    const inst = e.target.closest("[data-install]");
    if (inst) {
      inst.disabled = true; inst.textContent = t("Installing {0}…", "PyTorch");
      try { await api("POST", `/api/components/${inst.dataset.install}/install`); state.tools = (await api("GET", "/api/state")).tools; changed(); }
      catch (err) { inst.disabled = false; inst.textContent = err.message; }
      return;
    }
    if (e.target.closest("[data-reset=encoder]")) { s.encoder = {}; changed(); return; }
    const b = e.target.closest("[data-k=codec] button");
    if (b) { s.codec = b.dataset.v; changed(); }
  };
}

/** rigaya's encoder encodes this job: chosen, or "Auto" (GPU first, the default) with it installed. */
function usesRigaya(s, codec) {
  const rig = state.options.rigayaEncoders?.[`${codec}|${s.backend ?? "Software"}`];
  if (!rig) return false;
  const p = s.encoder?.program ?? "Auto";
  return p === "Rigaya" || (p === "Auto" && (state.tools?.rigaya ?? []).includes(s.backend));
}

/** FFmpeg or rigaya's encoder for a GPU backend (editor, Video → General): it decides which parameter pages appear. */
function encoderProgram(s, codec) {
  const o = state.options, e = s.encoder ?? {};
  const rig = o.rigayaEncoders?.[`${codec}|${s.backend ?? "Software"}`];
  if (!rig) return "";
  const ffEnc = o.encoderNames?.[`${codec}|${s.backend ?? "Software"}`] ?? "";
  const installed = (state.tools?.rigaya ?? []).includes(s.backend);
  const opt = (v, l, cur) => `<option value="${esc(v)}" ${String(cur ?? "") === v ? "selected" : ""}>${esc(l)}</option>`;
  return `<label class="field"><span>${esc(t("Encoder program"))}</span><select data-k="encoder.program">
      ${opt("Auto", t("Automatic: {0} when installed, otherwise FFmpeg", rig.name), e.program ?? "Auto")}${opt("FFmpeg", t("FFmpeg ({0})", ffEnc), e.program)}${opt("Rigaya", rig.name + " (rigaya)", e.program)}</select>
      <small class="muted">${esc(t("{0} reads the source with GPU decoding, resizes on the GPU and copies Dolby Vision / HDR10+ when no FFmpeg filter is needed (otherwise FFmpeg decodes and filters, and sends the frames to it). Install it in Settings → Components.", rig.name))}</small>
      ${e.program === "Rigaya" && !installed ? `<small class="warn">${esc(t("{0} is not installed: add it in Settings → Components, or choose FFmpeg as the encoder program.", rig.name))}</small>` : ""}</label>`;
}

/**
 * Editor, Video → Rate control: explicit values only (constant quality, bitrate or maximum size) plus speed and
 * structure; a quality level can fill them with KnA's values, it does not stay behind them.
 */
function editorRate(s, codec) {
  const o = state.options, e = s.encoder ?? {};
  const ffEnc = o.encoderNames?.[`${codec}|${s.backend ?? "Software"}`] ?? "";
  const rig = o.rigayaEncoders?.[`${codec}|${s.backend ?? "Software"}`];
  const useRig = usesRigaya(s, codec);
  const enc = useRig ? rig.name : ffEnc;
  const info = (useRig ? rig.info : o.encoders?.[ffEnc]) ?? { speeds: [], tunes: [] };
  let rc = e.rateControl ?? "Auto";
  if (rc === "Quality" && !info.qualityLabel) rc = "Auto";
  if (rc === "Auto" && info.qualityLabel) rc = "Quality";
  const opt = (v, l, cur) => `<option value="${esc(v)}" ${String(cur ?? "") === v ? "selected" : ""}>${esc(l)}</option>`;
  const higher = /vceencc|_amf|_videotoolbox|VCEEncC/i.test(enc);
  return `<small class="muted">${esc(t("Encoder: {0}.", enc))}</small>
    <label class="field"><span>${esc(t("Rate control"))}</span><select data-k="encoder.rateControl">
      ${info.qualityLabel ? opt("Quality", t("Constant quality ({0})", info.qualityLabel), rc) : opt("Auto", t("Quality level"), rc)}${opt("Bitrate", t("Average bitrate"), rc)}${opt("TargetSize", t("Maximum file size"), rc)}</select></label>
    ${rc === "Quality" ? `<div class="grid2"><label class="field"><span>${esc(t("{0} value ({1}–{2})", info.qualityLabel, info.qualityMin, info.qualityMax))}</span>
      <input type="number" step="0.5" min="${info.qualityMin}" max="${info.qualityMax}" data-k="encoder.qualityValue" value="${e.qualityValue ?? ""}">
      <small class="muted">${esc(higher ? t("Higher = better and bigger.") : t("Lower = better and bigger."))}</small></label>
      <label class="field"><span>${esc(t("Fill from a quality level"))}</span><select data-k="levelFill"><option value="">—</option>${o.qualities.map(q => `<option value="${esc(q.value)}">${esc(q.label)}</option>`).join("")}</select>
      <small class="muted">${esc(t("KnA's tuned values for {0}: from best to smallest.", enc))}</small></label></div>` : ""}
    ${rc === "Auto" ? `<label class="field"><span>${esc(t("Quality"))}</span><select data-k="quality">${o.qualities.map(q => `<option value="${esc(q.value)}" ${q.value === s.quality ? "selected" : ""}>${esc(q.label)}</option>`).join("")}</select>
      <small class="muted">${esc(t("KnA's tuned values for {0}: from best to smallest.", enc))}</small></label>` : ""}
    ${rc === "Bitrate" ? `<div class="grid2"><label class="field"><span>${esc(t("Bitrate (kb/s)"))}</span><input type="number" min="100" step="100" data-k="encoder.bitrateKbps" value="${e.bitrateKbps ?? 6000}"></label>
      <label class="field"><span>${esc(t("Maximum (kb/s, 0 = none)"))}</span><input type="number" min="0" step="100" data-k="encoder.maxBitrateKbps" value="${e.maxBitrateKbps ?? 0}"></label></div>` : ""}
    ${rc === "TargetSize" ? `<label class="field"><span>${esc(t("Maximum size (MB)"))}</span><input type="number" min="1" step="50" data-k="encoder.targetSizeMB" value="${e.targetSizeMB ?? 4096}">
      <small class="muted">${esc(t("As Windows counts it (1 MB = 1024 KB). KnA aims 3 % lower so one-pass encoders stay below."))}</small></label>
      <small class="muted" id="size-info">${esc(t("Audio, subtitles and the container are subtracted; the video gets the rest."))}</small>` : ""}
    ${useRig ? rigayaProfileField(rig, e) : ""}
    <div class="grid2">
      <label class="field"><span>${esc(t("Speed preset (fastest → slowest)"))}</span><select data-k="encoder.speed">${!e.speed || !info.speeds.length ? opt("", t("Encoder default"), e.speed) : ""}${info.speeds.map(v => opt(v, v, e.speed)).join("")}</select></label>
      <label class="field"><span>${esc(t("Tune"))}</span><select data-k="encoder.tune">${opt("", t("None"), e.tune)}${info.tunes.map(v => opt(v, v, e.tune)).join("")}</select></label>
    </div>
    <div class="grid3">
      <label class="field"><span>${esc(t("Keyframe every (s, 0 = auto)"))}</span><input type="number" min="0" step="0.5" data-k="encoder.keyframeSeconds" value="${e.keyframeSeconds ?? 0}"></label>
      <label class="field"><span>${esc(t("B-frames (−1 = auto)"))}</span><input type="number" min="-1" max="16" data-k="encoder.bFrames" value="${e.bFrames ?? -1}"></label>
      <label class="field"><span>${esc(t("Lookahead (frames, −1 = auto)"))}</span><input type="number" min="-1" max="250" data-k="encoder.lookahead" value="${e.lookahead ?? -1}"></label>
    </div>
    <small class="muted">${esc(t("Every other parameter of the encoder is on the next pages, one per group."))}</small>`;
}

/** Options the rigaya profiles write (mirror of RigayaProfiles.Owned). */
const profileOwned = ["--aq-temporal", "--split-enc", "--b-pyramid", "--vbaq", "--pe"];
const splitOpts = text => (text ?? "").match(/(?:"[^"]*"|\S)+/g)?.reduce((l, w) => { if (w.startsWith("-") && !/^-\d/.test(w)) l.push([w, null]); else if (l.length && l[l.length - 1][1] == null) l[l.length - 1][1] = w; else l.push([w, null]); return l; }, []) ?? [];
const joinOpts = l => l.map(([o, v]) => v == null ? o : `${o} ${v}`).join(" ");
/** The profile the encoder values match (mirror of RigayaProfiles.Current), or null. */
export function currentRigayaProfile(rig, e) {
  const own = joinOpts(splitOpts(e.extra).filter(([o]) => profileOwned.includes(o)));
  return (rig.profiles ?? []).find(p => (p.speed ?? "").toLowerCase() === (e.speed ?? "").toLowerCase() && (p.tune ?? null) === (e.tune ?? null)
    && (p.lookahead ?? null) === (e.lookahead ?? null) && joinOpts(splitOpts(p.extra)) === own) ?? null;
}
/** The encoder values with a profile written into them (mirror of RigayaProfiles.Apply). */
export function applyRigayaProfile(e, p) {
  const kept = joinOpts(splitOpts(e.extra).filter(([o]) => !profileOwned.includes(o)));
  const extra = [kept, p.extra ?? ""].map(x => x.trim()).filter(Boolean).join(" ");
  return { ...e, speed: p.speed, tune: p.tune ?? null, lookahead: p.lookahead ?? null, extra: extra || null };
}
function rigayaProfileField(rig, e) {
  const cur = currentRigayaProfile(rig, e);
  return `<label class="field"><span>${esc(t("{0} profile", rig.name))}</span><select data-k="rigayaProfile">
      ${cur ? "" : `<option value="" selected>${esc(t("Set by hand"))}</option>`}${(rig.profiles ?? []).map(p => `<option value="${esc(p.id)}" ${cur?.id === p.id ? "selected" : ""}>${esc(p.name)}</option>`).join("")}</select>
    <small class="muted">${esc(cur?.description ?? t("Speed, tune or options changed by hand: pick a profile to start again from its values."))}</small></label>
    <div class="gpu-estimate" hidden><small class="muted" id="gpu-estimate">${esc(t("Measure this GPU once (about a minute) to see how long this job takes with each profile."))}</small>
      <button type="button" class="btn sm" data-a="gpu-bench">${esc(t("Measure the GPU speed"))}</button></div>`;
}

/** Editor, Preset tab: what a saved preset decides for the other files (their automatic track plan). */
function presetRules(s, p) {
  const o = state.options;
  const opts = (list, cur) => list.map(x => `<option value="${esc(x.value)}" ${String(x.value) === String(cur) ? "selected" : ""}>${esc(x.label)}</option>`).join("");
  return `${p.usesSubtitleMode ? `<label class="field"><span>${esc(t("Subtitles"))}</span><select data-k="subtitles">${opts(o.subtitleModes, s.subtitles)}</select>
      <small class="muted">${esc(t("For the other files this preset is applied to: here the subtitles of this file are set in the Subtitles tab."))}</small></label>
      ${s.subtitles === "Hardsub" ? `<label class="toggle"><input type="checkbox" data-k="useSidecarSubtitles" ${s.useSidecarSubtitles ? "checked" : ""}><span class="switch"></span><span>${esc(t("Prefer external .ass/.srt next to the video"))}</span></label>` : ""}
      <label class="toggle"><input type="checkbox" data-k="ocrPictureSubtitles" ${s.ocrPictureSubtitles ? "checked" : ""}><span class="switch"></span><span>${esc(t("Convert picture subtitles (PGS, DVD) to text with OCR"))}</span></label>` : ""}
    ${p.usesAudio ? `<div class="grid2">
      <label class="field"><span>${esc(t("Audio"))}</span><select data-k="audioCodec">${opts(o.audioCodecs, s.audioCodec ?? "Auto")}</select></label>
      <label class="field"><span>${esc(t("Bitrate (kb/s)"))}</span><input type="number" min="8" max="1536" step="8" data-k="audioBitrate" value="${s.audioBitrate ?? ""}" placeholder="${esc(t("auto"))}"></label>
    </div>` : ""}
    ${p.encodesVideo || p.isRemux || p.isImages || p.isDemux ? trackSelection(s) : ""}`;
}

/** Hand-set encoder parameters (full form only); empty = KnA's values for the quality level. */
function advancedEncoder(s, codec) {
  const o = state.options, e = s.encoder ?? {};
  const ffEnc = o.encoderNames?.[`${codec}|${s.backend ?? "Software"}`] ?? "";
  const rig = o.rigayaEncoders?.[`${codec}|${s.backend ?? "Software"}`];
  const useRig = usesRigaya(s, codec);
  const enc = useRig ? rig.name : ffEnc;
  const info = (useRig ? rig.info : o.encoders?.[ffEnc]) ?? { speeds: [], tunes: [] };
  const opt = (v, l, cur) => `<option value="${esc(v)}" ${String(cur ?? "") === v ? "selected" : ""}>${esc(l)}</option>`;
  const empty = (e.program ?? "Auto") === "Auto" && !e.speed && !e.tune && e.keyframeSeconds == null && e.bFrames == null && e.lookahead == null && !e.extra;
  const installed = !rig || (state.tools?.rigaya ?? []).includes(s.backend);
  return `<details class="upscale" ${empty ? "" : "open"}><summary>${esc(t("Advanced encoder"))}${empty ? "" : " ●"}</summary>
    ${rig ? `<label class="field"><span>${esc(t("Encoder program"))}</span><select data-k="encoder.program">
      ${opt("Auto", t("Automatic: {0} when installed, otherwise FFmpeg", rig.name), e.program ?? "Auto")}${opt("FFmpeg", t("FFmpeg ({0})", ffEnc), e.program)}${opt("Rigaya", rig.name + " (rigaya)", e.program)}</select>
      <small class="muted">${esc(t("{0} reads the source with GPU decoding, resizes on the GPU and copies Dolby Vision / HDR10+ when no FFmpeg filter is needed (otherwise FFmpeg decodes and filters, and sends the frames to it). Install it in Settings → Components.", rig.name))}</small>
      ${useRig && !installed ? `<small class="warn">${esc(t("{0} is not installed: add it in Settings → Components, or choose FFmpeg as the encoder program.", rig.name))}</small>` : ""}</label>` : ""}
    <small class="muted">${esc(t("Encoder: {0}. Empty fields keep KnA's values for the chosen quality.", enc))}</small>
    <div class="grid2">
      <label class="field"><span>${esc(t("Speed preset (fastest → slowest)"))}</span><select data-k="encoder.speed">${opt("", t("From the quality level"), e.speed)}${info.speeds.map(v => opt(v, v, e.speed)).join("")}</select></label>
      <label class="field"><span>${esc(t("Tune"))}</span><select data-k="encoder.tune">${opt("", t("None"), e.tune)}${info.tunes.map(v => opt(v, v, e.tune)).join("")}</select></label>
    </div>
    <div class="grid3">
      <label class="field"><span>${esc(t("Keyframe every (s, 0 = auto)"))}</span><input type="number" min="0" step="0.5" data-k="encoder.keyframeSeconds" value="${e.keyframeSeconds ?? 0}"></label>
      <label class="field"><span>${esc(t("B-frames (−1 = auto)"))}</span><input type="number" min="-1" max="16" data-k="encoder.bFrames" value="${e.bFrames ?? -1}"></label>
      <label class="field"><span>${esc(t("Lookahead (frames, −1 = auto)"))}</span><input type="number" min="-1" max="250" data-k="encoder.lookahead" value="${e.lookahead ?? -1}"></label>
    </div>
    ${paramsBlock(t("All {0} parameters", enc), "video", "encoder.extra", e.extra, useRig ? "--aq-temporal --bref-mode each" : "-x265-params psy-rd=2:aq-strength=0.8", true)}
    ${empty ? "" : `<button type="button" class="btn sm ghost" data-reset="encoder">${esc(t("Back to automatic"))}</button>`}
  </details>`;
}

/** Quick panel: the rate control set by hand in the editor. */
function rateText(e) {
  if (e.rateControl === "Quality") return t("Constant quality {0} (set in the editor)", e.qualityValue ?? "?");
  if (e.rateControl === "Bitrate") return t("{0} kb/s (set in the editor)", e.bitrateKbps ?? 0);
  if (e.rateControl === "TargetSize") return t("Maximum {0} MB (set in the editor)", e.targetSizeMB ?? "?");
  return "";
}

/**
 * The one place where the video quality is decided: quality level (KnA's tuned values), constant quality, average
 * bitrate, or maximum file size (bitrates computed for every stream).
 */
function rateControl(s, codec) {
  const o = state.options, e = s.encoder ?? {};
  const ffEnc = o.encoderNames?.[`${codec}|${s.backend ?? "Software"}`] ?? "";
  const rig = o.rigayaEncoders?.[`${codec}|${s.backend ?? "Software"}`];
  const useRig = usesRigaya(s, codec);
  const info = (useRig ? rig.info : o.encoders?.[ffEnc]) ?? {};
  let rc = e.rateControl ?? "Auto";
  if (rc === "Quality" && !info.qualityLabel) rc = "Auto";
  const opt = (v, l) => `<option value="${esc(v)}" ${rc === v ? "selected" : ""}>${esc(l)}</option>`;
  return `<label class="field"><span>${esc(t("Rate control"))}</span><select data-k="encoder.rateControl">
      ${opt("Auto", t("Quality level"))}${info.qualityLabel ? opt("Quality", t("Constant quality ({0})", info.qualityLabel)) : ""}${opt("Bitrate", t("Average bitrate"))}${opt("TargetSize", t("Maximum file size"))}</select></label>
    ${rc === "Auto" ? `<label class="field"><span>${esc(t("Quality"))}</span><select data-k="quality">${o.qualities.map(q => `<option value="${esc(q.value)}" ${q.value === s.quality ? "selected" : ""}>${esc(q.label)}</option>`).join("")}</select>
      <small class="muted">${esc(t("KnA's tuned values for {0}: from best to smallest.", useRig ? rig.name : ffEnc))}</small></label>` : ""}
    ${rc === "Quality" ? `<label class="field"><span>${esc(t("{0} value ({1}–{2})", info.qualityLabel, info.qualityMin, info.qualityMax))}</span>
      <input type="number" step="0.5" min="${info.qualityMin}" max="${info.qualityMax}" data-k="encoder.qualityValue" value="${e.qualityValue ?? ""}">
      <small class="muted">${esc(t("Lower = better and bigger."))}</small></label>` : ""}
    ${rc === "Bitrate" ? `<div class="grid2"><label class="field"><span>${esc(t("Bitrate (kb/s)"))}</span><input type="number" min="100" step="100" data-k="encoder.bitrateKbps" value="${e.bitrateKbps ?? 6000}"></label>
      <label class="field"><span>${esc(t("Maximum (kb/s, 0 = none)"))}</span><input type="number" min="0" step="100" data-k="encoder.maxBitrateKbps" value="${e.maxBitrateKbps ?? 0}"></label></div>` : ""}
    ${rc === "TargetSize" ? `<label class="field"><span>${esc(t("Maximum size (MB)"))}</span><input type="number" min="1" step="50" data-k="encoder.targetSizeMB" value="${e.targetSizeMB ?? 4096}">
      <small class="muted">${esc(t("As Windows counts it (1 MB = 1024 KB). KnA aims 3 % lower so one-pass encoders stay below."))}</small></label>
      <small class="muted" id="size-info">${esc(t("Audio, subtitles and the container are subtracted; the video gets the rest."))}</small>` : ""}`;
}

/** Placeholder of a parameter editor, filled by mountParams (options read from the program's own help). */
function paramsBlock(title, tool, key, value, example, open = false) {
  const set = !!(value && String(value).trim());
  const inner = `<div class="params" data-tool="${esc(tool)}" data-pk="${esc(key)}" data-example="${esc(example)}"></div>`;
  return open ? `<div class="field"><span>${esc(title)}</span>${inner}</div>`
    : `<details class="upscale" ${set ? "open" : ""}><summary>${esc(title)}${set ? " ●" : ""}</summary>${inner}</details>`;
}

const optionCache = new Map();
/** "-opt value -flag" → [[opt, value|null]] (same rules as the server). */
function parsePairs(text) {
  const tokens = []; let cur = "", q = false;
  for (const ch of String(text ?? "")) {
    if (ch === '"') { q = !q; continue; }
    if (/\s/.test(ch) && !q) { if (cur) { tokens.push(cur); cur = ""; } continue; }
    cur += ch;
  }
  if (cur) tokens.push(cur);
  const out = [];
  for (let i = 0; i < tokens.length; i++) {
    if (!tokens[i].startsWith("-")) continue;
    const next = tokens[i + 1];
    const has = next != null && (!next.startsWith("-") || !isNaN(Number(next)));
    out.push([tokens[i], has ? tokens[++i] : null]);
  }
  return out;
}
const formatPairs = pairs => pairs.filter(([, v]) => v === null || v !== "").map(([k, v]) => v === null ? k : `${k} ${/\s/.test(v) ? `"${v}"` : v}`).join(" ");

/** Every parameter of the program that runs for each `.params` block: searchable list, one row per chosen option. */
function mountParams(root, s, onChange) {
  $$(".params", root).forEach(async el => {
    const tool = el.dataset.tool, key = el.dataset.pk;
    const [a, b] = key.split(".");
    const get = () => (b ? s[a]?.[b] : s[a]) ?? "";
    const set = v => { const val = v.trim() || null; if (b) s[a] = { ...s[a], [b]: val }; else s[a] = val; onChange(); };
    let pairs = parsePairs(get());
    const listId = "pl-" + Math.random().toString(36).slice(2);
    el.innerHTML = `<div class="row" style="gap:6px"><input class="mono grow" list="${listId}" placeholder="${esc(t("Search a parameter (name or description)…"))}">
      <button type="button" class="btn sm ghost" data-a="as-text">${esc(t("Edit as text"))}</button></div>
      <datalist id="${listId}"></datalist><small class="muted" data-status>${esc(t("Reading the program's options…"))}</small>
      <div data-rows></div><input class="mono" data-raw hidden placeholder="${esc(el.dataset.example)}">`;
    const search = el.querySelector("input[list]"), rows = el.querySelector("[data-rows]"), raw = el.querySelector("[data-raw]");
    let options = [];
    const emit = () => { const txt = formatPairs(pairs); raw.value = txt; set(txt); };
    const render = () => {
      rows.innerHTML = pairs.length ? pairs.map(([k, v], i) => {
        const o = options.find(x => x.name === k);
        const input = v === null && (!o || !o.argument) ? `<small class="muted">${esc(t("switch (no value)"))}</small>`
          : o?.values?.length ? `<select data-i="${i}">${[...new Set([v ?? "", ...o.values])].filter(x => x !== "").map(x => `<option ${x === v ? "selected" : ""}>${esc(x)}</option>`).join("")}</select>`
          : `<input class="mono" data-i="${i}" value="${esc(v ?? "")}" placeholder="${esc(o?.argument ?? "")}">`;
        return `<div class="param-row"><code>${esc(k)}</code>${input}<button type="button" class="btn icon ghost sm" data-rm="${i}" title="${esc(t("Remove"))}"><svg><use href="#i-x"/></svg></button>
          ${o?.description ? `<small class="muted">${esc(o.description)}</small>` : options.length ? `<small class="muted">${esc(t("Not in this program's help: passed on as written."))}</small>` : ""}</div>`;
      }).join("") : `<small class="muted">${esc(t("No parameter set by hand: KnA's values are used."))}</small>`;
    };
    raw.value = formatPairs(pairs);
    render();
    el.onchange = e => {
      e.stopPropagation();   // not a form field of the outer form
      if (e.target === raw) { pairs = parsePairs(raw.value); render(); emit(); return; }
      if (e.target === search) {
        const name = search.value.split(" ")[0];
        const o = options.find(x => x.name === name) ?? (name.startsWith("-") ? { name, argument: "value", values: [] } : null);
        search.value = "";
        if (o && !pairs.some(([k]) => k === o.name)) { pairs.push([o.name, o.argument ? (o.values?.[0] ?? "") : null]); render(); emit(); }
        return;
      }
      if (e.target.dataset.i != null) { pairs[+e.target.dataset.i][1] = e.target.value; emit(); }
    };
    el.onclick = e => {
      const rm = e.target.closest("[data-rm]");
      if (rm) { pairs.splice(+rm.dataset.rm, 1); render(); emit(); }
      if (e.target.closest("[data-a=as-text]")) { raw.hidden = !raw.hidden; rows.hidden = !raw.hidden; }
    };
    try {
      const ck = tool + "|" + JSON.stringify([s.codec, s.backend, s.encoder?.program, s.audioCodec, s.container, s.profile]);
      const r = optionCache.get(ck) ?? await api("POST", "/api/tools/options", { tool, settings: s });
      optionCache.set(ck, r);
      options = r.options ?? [];
      el.querySelector("datalist").innerHTML = options.map(o => `<option value="${esc(o.name + (o.argument ? " <" + o.argument + ">" : "") + " — " + o.description)}"></option>`).join("");
      el.querySelector("[data-status]").textContent = options.length
        ? t("{0} parameters of {1}, read from its help. Applied last: they win over KnA's values.", options.length, r.program)
        : t("{0}: its help could not be read; parameters can still be written as text.", r.program);
      render();
    } catch (err) { el.querySelector("[data-status]").textContent = err.message; }
  });
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
export { parsePairs, formatPairs };
