// Sign-in, first start (create the administrator), own password and the administrators' account list.
import { $, esc, state, t, api, post, toast, guard, fillSelect } from "./core.js";

// ---------------------------------------------------------------- sign in / first start
/** The signed-in user; when nobody is, shows the sign-in (or "create the administrator") screen and waits. */
export async function signIn() {
  const st = await api("GET", "/api/auth/status");
  if (st.user) return st.user;
  const setup = st.setupRequired;
  $("#auth").hidden = false;
  $("#auth-intro").textContent = setup
    ? t("First start: create the administrator account. It manages the server and the other accounts.")
    : t("Sign in to KnA.");
  $("#auth-pw2-row").hidden = !setup;
  $("#auth-pw").autocomplete = setup ? "new-password" : "current-password";
  $("#auth-pw").minLength = setup ? st.minPasswordLength : 0;
  $("#auth-submit").textContent = setup ? t("Create the administrator") : t("Sign in");
  if (setup && !$("#auth-name").value) $("#auth-name").value = "admin";
  ($("#auth-name").value ? $("#auth-pw") : $("#auth-name")).focus();
  return new Promise(resolve => {
    $("#auth-form").onsubmit = async e => {
      e.preventDefault();
      const err = $("#auth-error");
      err.textContent = "";
      const name = $("#auth-name").value.trim(), password = $("#auth-pw").value;
      if (!name || !password) { err.textContent = t("Enter the user name and the password."); return; }
      if (setup && password.length < st.minPasswordLength) { err.textContent = t("The password needs at least {0} characters.", st.minPasswordLength); return; }
      if (setup && password !== $("#auth-pw2").value) { err.textContent = t("The two passwords differ."); return; }
      $("#auth-submit").disabled = true;
      try {
        const r = await post(setup ? "/api/auth/setup" : "/api/auth/login", { name, password, remember: $("#auth-remember").checked });
        $("#auth").hidden = true;
        $("#auth-pw").value = $("#auth-pw2").value = "";
        resolve(r.user);
      } catch (ex) {
        err.textContent = ex.message;
      } finally { $("#auth-submit").disabled = false; }
    };
  });
}

// ---------------------------------------------------------------- the signed-in user (top bar)
export function initUserUi(user) {
  state.user = user;
  const accountsOn = !user.authDisabled;
  $("#btn-user").hidden = !accountsOn;
  $("#user-name").textContent = user.name;
  $("#btn-user").title = user.isAdmin ? t("{0} (administrator)", user.name) : user.name;
  // Users encode; the server's settings, components and accounts are the administrators'.
  $("#btn-settings").hidden = !user.isAdmin;
  $("#btn-detect").hidden = !user.isAdmin;
  $("#s-tab-accounts").hidden = !user.isAdmin || !accountsOn;
  $("#btn-user").onclick = () => {
    $("#user-title").textContent = user.name;
    $("#user-info").textContent = (user.isAdmin ? t("Administrator") : t("User"))
      + (user.isAdmin ? "" : " · " + (user.advanced ? t("advanced mode allowed") : t("no advanced mode"))
        + " · " + t("folders: {0}", (user.roots ?? []).join(", ") || "–"));
    $("#user-login").value = user.name;
    for (const id of ["#user-pw-old", "#user-pw-new", "#user-pw-new2"]) $(id).value = "";
    $("#dlg-user").showModal();
  };
}
$("#btn-logout").onclick = guard(async () => {
  await post("/api/auth/logout");
  location.reload();
});
$("#btn-change-pw").onclick = guard(async () => {
  const pw = $("#user-pw-new").value;
  if (pw !== $("#user-pw-new2").value) throw new Error(t("The two passwords differ."));
  await post("/api/auth/password", { current: $("#user-pw-old").value, password: pw });
  $("#dlg-user").close();
  toast(t("Password changed"), "ok");
});

// ---------------------------------------------------------------- administrators: accounts (Settings → Accounts)
let accounts = [], editing = null, serverRoots = [];

export async function loadAccounts() {
  accounts = await api("GET", "/api/accounts");
  serverRoots = (await api("GET", "/api/info")).roots ?? [];
  renderAccounts();
}

const when = d => d ? new Date(d).toLocaleString() : t("never");

function renderAccounts() {
  $("#acc-list").innerHTML = accounts.map(a => `
    <div class="acc-row ${a.disabled ? "off" : ""}" data-id="${esc(a.id)}">
      <b>${esc(a.name)}</b>
      <span class="chip ${a.isAdmin ? "hw" : ""}">${esc(a.isAdmin ? t("Administrator") : t("User"))}</span>
      ${a.disabled ? `<span class="chip">${esc(t("Disabled"))}</span>` : ""}
      ${!a.isAdmin && a.permissions.advanced ? `<span class="chip bit">${esc(t("Advanced"))}</span>` : ""}
      ${a.name === state.user?.name ? `<span class="chip">${esc(t("you"))}</span>` : ""}
      <span class="grow"></span>
      <button class="btn sm ghost" type="button" data-act="edit">${esc(t("Edit"))}</button>
      <button class="btn sm ghost" type="button" data-act="signout" ${a.sessions ? "" : "disabled"} title="${esc(t("Sign out on every device"))}">${esc(t("Sign out"))} (${a.sessions})</button>
      <button class="btn sm ghost" type="button" data-act="delete" title="${esc(t("Delete"))}"><svg><use href="#i-trash"/></svg></button>
      <span class="note">${esc(t("Last sign-in: {0}", when(a.lastLogin)))}${!a.isAdmin ? " · " + esc(t("folders: {0}", a.permissions.roots?.join(", ") ?? t("all of the server's"))) : ""}${a.fromEnvironment ? " · " + esc(t("created from KNA_PASSWORD")) : ""}</span>
    </div>`).join("");
}

$("#acc-list").onclick = guard(async e => {
  const b = e.target.closest("[data-act]"); if (!b) return;
  const a = accounts.find(x => x.id === b.closest("[data-id]").dataset.id);
  if (b.dataset.act === "edit") openEdit(a);
  else if (b.dataset.act === "signout") { await post(`/api/accounts/${a.id}/signout`); toast(t("{0} signed out everywhere", a.name), "ok"); await loadAccounts(); }
  else if (b.dataset.act === "delete") {
    if (!confirm(t("Delete the account \"{0}\"?", a.name))) return;
    await api("DELETE", `/api/accounts/${a.id}`);
    await loadAccounts();
  }
});

function openEdit(a) {
  editing = a ?? null;
  $("#acc-edit").hidden = false;
  $("#acc-edit-title").textContent = a ? t("Edit \"{0}\"", a.name) : t("New account");
  $("#acc-name").value = a?.name ?? "";
  $("#acc-pw").value = "";
  $("#acc-pw-note").textContent = a ? t("Leave empty to keep the current one.") : t("At least {0} characters.", 8);
  fillSelect($("#acc-role"), [{ value: "User", label: t("User") }, { value: "Admin", label: t("Administrator") }], a?.role ?? "User");
  $("#acc-advanced").checked = a?.permissions.advanced ?? false;
  $("#acc-disabled").checked = a?.disabled ?? false;
  $("#acc-roots").value = (a?.permissions.roots ?? []).join("\n");
  $("#acc-roots-note").textContent = t("Empty = every folder of the server: {0}", serverRoots.join(", ") || "–");
  const adminRole = () => { const admin = $("#acc-role").value === "Admin"; $("#acc-advanced").disabled = admin; $("#acc-roots").disabled = admin; };
  $("#acc-role").onchange = adminRole; adminRole();
  $("#acc-name").focus();
}
$("#acc-add").onclick = () => openEdit(null);
$("#acc-cancel").onclick = () => { $("#acc-edit").hidden = true; editing = null; };
$("#acc-save").onclick = guard(async () => {
  const body = {
    name: $("#acc-name").value.trim(), role: $("#acc-role").value, disabled: $("#acc-disabled").checked,
    permissions: { advanced: $("#acc-advanced").checked, roots: $("#acc-roots").value.split("\n").map(x => x.trim()).filter(Boolean) },
  };
  const pw = $("#acc-pw").value;
  if (editing) await api("PUT", `/api/accounts/${editing.id}`, { ...body, password: pw || null });
  else await post("/api/accounts", { ...body, password: pw });
  toast(t("Account \"{0}\" saved", body.name), "ok");
  $("#acc-edit").hidden = true; editing = null;
  await loadAccounts();
});
