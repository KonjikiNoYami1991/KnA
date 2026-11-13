// Settings form shared by the side panel ("quick") and the editor Output tab ("full").
import { $, $$, esc, state, t, label, profileOf, fillSelect, capability } from "./core.js";

function backendStatus(codec, backend, tenBit) {
  const c = capability(codec, backend);
  if (!state.settings.capabilities) return { ok: true, text: "" };
  if (!c) return { ok: false, text: t("not available for this codec") };
  if (tenBit ? c.works10Bit : c.works8Bit) return { ok: true, text: "" };
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
  const tenBitPossible = ["HEVC", "AV1"].includes(codec) && ["UniversalMkv", "Streaming"].includes(p.value);
  if (!p.codecs.includes(s.codec) && p.codecs.length) s.codec = p.codecs[0];
  if (!p.allowsHardware) s.backend = "Software";
  const backends = o.backends.map(b => {
    const st = backendStatus(codec, b.value, s.tenBit && tenBitPossible);
    return { value: b.value, label: b.label + (st.ok ? "" : " — " + st.text), disabled: !st.ok };
  });
  if (backends.find(b => b.value === s.backend)?.disabled) s.backend = backends.find(b => !b.disabled)?.value ?? "Software";

  const show = {
    codec: p.codecs.length > 1,
    encoder: p.encodesVideo || p.value === "RemuxMp4",
    tenBit: p.encodesVideo,
    res: p.usesResolution, quality: p.usesQuality, subs: p.usesSubtitleMode,
    hdr: p.usesVideoFilters, images: p.isImages, demux: p.isDemux,
    container: full && (opts.allowedContainers?.length ?? 0) > 1,
  };
  root.innerHTML = `
    <label class="field"><span>${esc(t("Profile"))}</span><select data-k="profile"></select>
      <small class="muted">${esc(p.description)}</small></label>
    ${show.codec ? `<div class="field"><span>${esc(t("Video codec"))}</span><div class="segmented" data-k="codec">
      ${p.codecs.map(c => `<button type="button" data-v="${c}" class="${c === s.codec ? "on" : ""}">${esc(label(o.codecs, c).split(" /")[0])}</button>`).join("")}
    </div></div>` : ""}
    ${show.encoder ? `<label class="field"><span>${esc(t("Encoder"))}</span><select data-k="backend" ${p.allowsHardware ? "" : "disabled"}></select>
      <small class="muted">${esc(!p.allowsHardware ? t("This profile is CPU only.") : s.backend === "Software" ? t("CPU encoding: slowest, best compression.") : t("Hardware encoding: much faster, slightly bigger files."))}</small></label>` : ""}
    ${show.tenBit ? `<label class="toggle ${tenBitPossible ? "" : "disabled"}"><input type="checkbox" data-k="tenBit" ${s.tenBit && tenBitPossible ? "checked" : ""} ${tenBitPossible ? "" : "disabled"}>
      <span class="switch"></span><span>${esc(t("10-bit output"))} <small class="muted">(HEVC / AV1)</small></span></label>` : ""}
    ${(show.res || show.quality) ? `<div class="grid2">
      ${show.res ? `<label class="field"><span>${esc(t("Resolution"))}</span><select data-k="resolution"></select></label>` : "<div></div>"}
      ${show.quality ? `<label class="field"><span>${esc(t("Quality"))}</span><select data-k="quality"></select></label>` : ""}
    </div>` : ""}
    ${show.subs ? `<label class="field"><span>${esc(t("Subtitles"))}</span><select data-k="subtitles"></select>
      <small class="muted">${esc(t("Automatic plan: the editor (Tracks tab) can override it per file."))}</small></label>
      ${s.subtitles === "Hardsub" ? `<label class="toggle"><input type="checkbox" data-k="useSidecarSubtitles" ${s.useSidecarSubtitles ? "checked" : ""}><span class="switch"></span><span>${esc(t("Prefer external .ass/.srt next to the video"))}</span></label>` : ""}` : ""}
    ${show.container ? `<label class="field"><span>${esc(t("Container"))}</span><select data-k="container"></select></label>` : ""}
    ${full && show.hdr ? `<label class="field"><span>HDR</span><select data-k="hdr"></select></label>` : ""}
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
  `;
  const sel = k => $(`[data-k="${k}"]`, root);
  fillSelect(sel("profile"), o.profiles, s.profile);
  if (show.encoder) fillSelect(sel("backend"), backends, s.backend);
  if (show.res) fillSelect(sel("resolution"), o.resolutions, s.resolution);
  if (show.quality) fillSelect(sel("quality"), o.qualities, s.quality);
  if (show.subs) fillSelect(sel("subtitles"), o.subtitleModes, s.subtitles);
  if (show.container) fillSelect(sel("container"), o.containers.filter(c => opts.allowedContainers.includes(c.value)), s.container ?? p.container);
  if (full && show.hdr) fillSelect(sel("hdr"), o.hdrModes, s.hdr);
  if (show.images) { fillSelect(sel("images.format"), o.imageFormats, s.images.format); fillSelect(sel("images.mode"), o.imageModes, s.images.mode); }

  const changed = () => { renderSettingsForm(root, s, opts); opts.onChange?.(s); };
  root.onchange = e => {
    const k = e.target.dataset.k; if (!k) return;
    let v = e.target.type === "checkbox" ? e.target.checked : e.target.value;
    if (["resolution"].includes(k)) v = Number(v);
    if (["images.intervalSeconds", "images.count", "images.quality", "chunks.workers", "chunks.targetSeconds"].includes(k)) v = Number(v);
    const [a, b] = k.split(".");
    if (b) s[a] = { ...s[a], [b]: v }; else s[a] = v;
    if (k === "profile") { s.container = null; }
    changed();
  };
  root.onclick = e => {
    const b = e.target.closest("[data-k=codec] button");
    if (b) { s.codec = b.dataset.v; changed(); }
  };
}

export function cloneSettings(s) { return JSON.parse(JSON.stringify(s)); }
