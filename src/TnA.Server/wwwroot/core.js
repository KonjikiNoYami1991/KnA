// Shared helpers: DOM, API, i18n, formatting, global state.
export const $ = (s, el = document) => el.querySelector(s);
export const $$ = (s, el = document) => [...el.querySelectorAll(s)];
export const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

export const state = {
  options: null, settings: null, tools: null, queue: null, presets: [],
  jobs: new Map(), order: [], selected: new Set(), form: null,
  browse: { path: null, selected: new Set(), external: false, onPick: null },
  advancedAllowed: false, powerActionsAllowed: false,
};

// ---------------------------------------------------------------- i18n
// English text is the key; the server sends the Italian table when the browser is Italian.
let dict = {};
// Language: the server setting wins; "Auto" follows the browser (sent as Accept-Language).
export const browserLang = (navigator.language || "en").toLowerCase().startsWith("it") ? "it" : "en";
export let lang = browserLang;
export async function loadI18n() {
  try {
    const r = await (await fetch("/api/i18n", { headers: { "Accept-Language": browserLang } })).json();
    lang = r.lang; dict = r.table || {};
  } catch { dict = {}; }
  document.documentElement.lang = lang;
}
export function t(text, ...args) {
  let s = dict[text] ?? text;
  args.forEach((a, i) => { s = s.replaceAll(`{${i}}`, a); });
  return s;
}
export function translatePage(root = document) {
  $$("[data-i18n]", root).forEach(el => { el.dataset.i18nKey ??= el.textContent.trim(); el.textContent = t(el.dataset.i18nKey); });
  $$("[data-i18n-title]", root).forEach(el => { el.title = t(el.dataset.i18nTitle); });
  $$("[data-i18n-ph]", root).forEach(el => { el.placeholder = t(el.dataset.i18nPh); });
}

// ---------------------------------------------------------------- API
export async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: { ...(body ? { "Content-Type": "application/json" } : {}), "Accept-Language": browserLang },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) {
    let msg = res.statusText;
    try { const j = await res.json(); msg = j.detail || j.title || msg; } catch { /* not json */ }
    throw new Error(msg);
  }
  const type = res.headers.get("content-type") || "";
  return type.includes("json") ? res.json() : res.text();
}
export const post = (u, b) => api("POST", u, b ?? {});

export function toast(text, kind = "") {
  const el = document.createElement("div");
  el.className = "toast " + kind;
  el.textContent = text;
  $("#toasts").append(el);
  setTimeout(() => el.remove(), kind === "err" ? 7000 : 3500);
}
export const guard = fn => async (...a) => { try { await fn(...a); } catch (e) { toast(e.message, "err"); } };

// ---------------------------------------------------------------- formatting
export const fmtTime = s => {
  if (s == null || !isFinite(s)) return "–";
  s = Math.max(0, Math.round(s));
  const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), x = s % 60;
  return (h ? h + ":" + String(m).padStart(2, "0") : m) + ":" + String(x).padStart(2, "0");
};
/** hh:mm:ss.mmm */
export const fmtStamp = s => {
  s = Math.max(0, s || 0);
  const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), sec = s % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:${sec.toFixed(3).padStart(6, "0")}`;
};
export const parseStamp = text => {
  if (text == null || String(text).trim() === "") return null;
  const parts = String(text).trim().replace(",", ".").split(":").map(Number);
  if (parts.some(isNaN)) return null;
  return parts.reduce((acc, p) => acc * 60 + p, 0);
};
export const fmtSize = b => {
  if (!b) return "–";
  const u = ["B", "KB", "MB", "GB", "TB"]; let i = 0;
  while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; }
  return b.toFixed(i >= 2 ? 1 : 0) + " " + u[i];
};
export const label = (list, v) => list.find(o => o.value === String(v))?.label ?? v;
export const profileOf = v => state.options.profiles.find(p => p.value === v);
export const debounce = (fn, ms) => { let h; return (...a) => { clearTimeout(h); h = setTimeout(() => fn(...a), ms); }; };

export function fillSelect(sel, items, value) {
  sel.innerHTML = items.map(o => `<option value="${esc(o.value)}" ${o.disabled ? "disabled" : ""}>${esc(o.label)}</option>`).join("");
  if (value != null) sel.value = String(value);
}

export function showText(title, text) {
  $("#text-title").textContent = title;
  $("#text-body").textContent = text;
  $("#dlg-text").showModal();
}

export function capability(codec, backend) {
  return state.settings.capabilities?.encoders?.find(e => e.codec === codec && e.backend === backend);
}
