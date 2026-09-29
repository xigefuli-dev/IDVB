const state = { mode: "login", user: null, turnstileRequired: true, turnstileSiteKey: null, turnstileWidgetId: null };
const authDialog = document.querySelector("#auth-dialog");
const authShell = document.querySelector(".auth-shell");
const profileDialog = document.querySelector("#profile-dialog");
const oauthDialog = document.querySelector("#oauth-dialog");
const authForm = document.querySelector("#auth-form");
const formMessage = document.querySelector("#form-message");
document.querySelectorAll("[data-auth-mode]").forEach((button) => {
  button.addEventListener("click", () => openAuth(button.dataset.authMode));
});
document.querySelectorAll(".auth-tabs [data-tab]").forEach((button) => {
  button.addEventListener("click", () => setMode(button.dataset.tab));
});
document.querySelector("#auth-close").addEventListener("click", () => authDialog.close());
document.querySelector("#profile-close").addEventListener("click", () => profileDialog.close());
document.querySelector("#profile-button").addEventListener("click", openProfile);
document.querySelector("#logout-button").addEventListener("click", logout);
document.querySelector("#avatar-input").addEventListener("change", uploadAvatar);
document.querySelector("#oauth-allow").addEventListener("click", allowOAuth);
document.querySelector("#oauth-deny").addEventListener("click", denyOAuth);
document.querySelector("#forgot-password").addEventListener("click", () => setMode("reset-request"));
document.querySelector("#builder-entry-btn")?.addEventListener("click", openBuilderConsole);
document.querySelector("#builder-back-btn")?.addEventListener("click", closeBuilderConsole);
document.querySelectorAll(".builder-nav-item").forEach((button) => {
  button.addEventListener("click", () => switchBuilderTab(button.dataset.tab));
});
document.querySelector("#user-search-input")?.addEventListener("input", renderBuilderUsers);
document.querySelector("#map-search-input")?.addEventListener("input", renderBuilderMaps);
document.querySelector("#announcement-search-input")?.addEventListener("input", renderBuilderAnnouncements);
document.querySelector("#announcement-category-filter")?.addEventListener("change", renderBuilderAnnouncements);
document.querySelector("#btn-create-announcement")?.addEventListener("click", () => openAnnouncementDialog(null));
document.querySelector("#announcement-dialog-close")?.addEventListener("click", () => document.querySelector("#announcement-dialog").close());
document.querySelector("#announcement-cancel-btn")?.addEventListener("click", () => document.querySelector("#announcement-dialog").close());
document.querySelector("#announcement-form")?.addEventListener("submit", submitAnnouncementForm);
document.querySelectorAll("[data-announcement-template]").forEach((btn) => {
  btn.addEventListener("click", () => applyAnnouncementTemplate(btn.dataset.announcementTemplate));
});
document.querySelector("#feedbacks-list")?.addEventListener("click", handleFeedbackActionClick);
authDialog.addEventListener("click", closeOnBackdrop);
profileDialog.addEventListener("click", closeOnBackdrop);
document.querySelector("#announcement-dialog")?.addEventListener("click", closeOnBackdrop);
authDialog.addEventListener("close", syncDialogState);
profileDialog.addEventListener("close", syncDialogState);
document.querySelector("#announcement-dialog")?.addEventListener("close", syncDialogState);
authForm.addEventListener("submit", submitAuth);
const mapSearch = document.querySelector("#map-search");
mapSearch.addEventListener("input", filterMaps);

restoreSession();
loadAuthCapabilities();
loadMaps();
function filterMaps() {
  const query = mapSearch.value.trim().toLocaleLowerCase("zh-CN");
  let visible = 0;
  document.querySelectorAll(".map-card").forEach((card) => {
    card.hidden = !card.dataset.name.toLocaleLowerCase("zh-CN").includes(query);
    if (!card.hidden) visible += 1;
  });
  document.querySelector("#empty-state").hidden = visible !== 0;
}

async function loadMaps() {
  const grid = document.querySelector("#map-grid");
  const count = document.querySelector("#map-count");
  try {
    const { maps, publications = [] } = await api("/api/maps");
    const entries = [...publications.map((publication) => ({
      name: publication.name,
      publisherName: publication.publisherName,
      coverUrl: publication.coverUrl,
      filename: `订阅版本 ${publication.version}`,
      uploaded: publication.publishedAt,
      downloadUrl: publication.subscriptionLink,
      subscription: true,
    })), ...maps];
    entries.forEach((map) => grid.append(createMapCard(map)));
    count.textContent = `${entries.length} 个发布`;
    filterMaps();
  } catch {
    count.textContent = "地图读取失败";
    document.querySelector("#empty-state").textContent = "地图暂时无法载入，请稍后再试。";
    document.querySelector("#empty-state").hidden = false;
  }
}

function createMapCard(map) {
  const card = document.createElement("a");
  card.className = "map-card";
  card.dataset.name = map.name;
  card.href = map.downloadUrl;
  if (map.subscription) card.addEventListener("click", async (event) => {
    event.preventDefault();
    await navigator.clipboard.writeText(map.downloadUrl);
    const toast = document.querySelector("#copy-toast");
    toast.hidden = false;
    toast.classList.remove("copy-toast-enter");
    void toast.offsetWidth;
    toast.classList.add("copy-toast-enter");
  });

  const art = document.createElement("div");
  art.className = "map-art";
  if (map.coverUrl) {
    const image = document.createElement("img");
    image.src = map.coverUrl;
    image.alt = "";
    art.append(image);
  } else art.innerHTML = '<span class="route-line"></span><span class="pin"></span>';
  const body = document.createElement("div");
  body.className = "map-body";
  const title = document.createElement("div");
  title.className = "map-title";
  const copy = document.createElement("div");
  const type = document.createElement("p");
  type.textContent = map.subscription ? "IDVB 地图更新订阅" : ".idvm 地图包";
  const heading = document.createElement("h2");
  heading.textContent = map.name;
  const arrow = document.createElement("span");
  arrow.textContent = "↘";
  const footer = document.createElement("footer");
  const date = document.createElement("span");
  date.textContent = new Date(map.uploaded).toLocaleDateString("zh-CN");
  const size = document.createElement("span");
  size.textContent = map.subscription ? `作者：${map.publisherName}` : formatSize(map.size);

  copy.append(type, heading);
  title.append(copy, arrow);
  footer.append(date, size);
  body.append(title, footer);
  card.append(art, body);
  return card;
}

function formatSize(bytes) {
  return bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.ceil(bytes / 1024)} KB`;
}

function openAuth(mode) {
  setMode(mode);
  authDialog.showModal();
  renderTurnstile();
  document.body.classList.add("dialog-open");
  window.setTimeout(() => document.querySelector(mode === "register" ? "#display-name" : "#email").focus(), 50);
}

function renderTurnstile() {
  if (!state.turnstileRequired || state.turnstileWidgetId !== null || !authDialog.open) return;
  if (!state.turnstileSiteKey) return;
  if (!window.turnstile) {
    window.setTimeout(renderTurnstile, 100);
    return;
  }
  state.turnstileWidgetId = window.turnstile.render("#turnstile-widget", {
    sitekey: state.turnstileSiteKey,
    theme: "light",
  });
}

function setMode(mode) {
  const previousShape = authDialog.open ? cardShape() : null;
  state.mode = ["register", "reset-request", "reset-confirm"].includes(mode) ? mode : "login";
  authShell.dataset.mode = state.mode;
  clearErrors();
  document.querySelectorAll("[data-tab]").forEach((tab) => {
    const selected = tab.dataset.tab === state.mode || (tab.dataset.tab === "login" && state.mode.startsWith("reset-"));
    tab.setAttribute("aria-selected", String(selected));
    tab.tabIndex = selected ? 0 : -1;
  });
  document.querySelector(".register-field").hidden = state.mode !== "register";
  document.querySelector("#display-name").required = state.mode === "register";
  document.querySelector(".login-field").hidden = state.mode !== "login";
  document.querySelector(".reset-code-field").hidden = state.mode !== "reset-confirm";
  document.querySelector("#reset-code").required = state.mode === "reset-confirm";
  document.querySelector("#password").closest(".field").hidden = state.mode === "reset-request";
  document.querySelector("#password").required = state.mode !== "reset-request";
  document.querySelector("#password").autocomplete = ["register", "reset-confirm"].includes(state.mode) ? "new-password" : "current-password";
  document.querySelector("#submit-label").textContent = ({ register: "创建账户", login: "登录",
    "reset-request": "发送验证码", "reset-confirm": "更改密码" })[state.mode];
  authForm.classList.remove("auth-mode-switch");
  void authForm.offsetWidth;
  authForm.classList.add("auth-mode-switch");
  if (previousShape) authShell.animate([previousShape, cardShape()], { duration: 240, easing: "cubic-bezier(.2,.8,.2,1)" });
}

function cardShape() {
  const bounds = authShell.getBoundingClientRect();
  return { width: `${bounds.width}px`, height: `${bounds.height}px`, borderRadius: getComputedStyle(authShell).borderRadius };
}

async function submitAuth(event) {
  event.preventDefault();
  clearErrors();
  const values = Object.fromEntries(new FormData(authForm));
  if (state.turnstileRequired) {
    values.turnstileToken = values["cf-turnstile-response"] || "";
    if (!values.turnstileToken) { formMessage.textContent = "请先完成人机验证后再继续。"; return; }
  }
  const submit = authForm.querySelector("button[type=submit]");
  submit.disabled = true;
  formMessage.textContent = ({ register: "正在创建安全账户…", login: "正在验证账户…",
    "reset-request": "正在发送验证码…", "reset-confirm": "正在更改密码…" })[state.mode];
  try {
    const path = state.mode.startsWith("reset-")
      ? `/api/auth/password-reset/${state.mode === "reset-request" ? "request" : "confirm"}`
      : `/api/auth/${state.mode}`;
    const result = await api(path, { method: "POST", body: JSON.stringify(values) });
    await loadAuthCapabilities();
    if (state.mode === "reset-request") {
      setMode("reset-confirm");
      formMessage.classList.add("success");
      formMessage.textContent = result.message;
      document.querySelector("#reset-code").focus();
      return;
    }
    if (state.mode === "reset-confirm") {
      setMode("login");
      formMessage.classList.add("success");
      formMessage.textContent = result.message;
      document.querySelector("#password").value = "";
      document.querySelector("#password").focus();
      return;
    }
    setUser(result.user);
    formMessage.classList.add("success");
    formMessage.textContent = state.mode === "register" ? "账户已创建，欢迎加入 IDVB 地图社区。" : "登录成功。";
    window.setTimeout(() => {
      presentOAuthIfRequested().then((presented) => {
        if (!presented) authDialog.close();
        authForm.reset();
      });
    }, 450);
  } catch (error) {
    formMessage.textContent = error.message;
    if (error.code === "turnstile_required") await loadAuthCapabilities();
    if (state.turnstileRequired) window.turnstile?.reset();
    for (const [field, message] of Object.entries(error.fields || {})) {
      const input = authForm.elements[field];
      if (input) input.setAttribute("aria-invalid", "true");
      const target = document.querySelector(`[data-error-for="${field}"]`);
      if (target) target.textContent = message;
    }
  } finally {
    submit.disabled = false;
  }
}

async function loadAuthCapabilities() {
  try {
    const capabilities = await api("/api/auth/capabilities");
    state.turnstileRequired = capabilities.turnstileRequired;
    state.turnstileSiteKey = capabilities.turnstileSiteKey;
    document.querySelector("#turnstile-field").hidden = !state.turnstileRequired;
    renderTurnstile();
  } catch { formMessage.textContent = "人机验证服务暂时不可用，请稍后重试。"; }
}

async function restoreSession() {
  try {
    const result = await api("/api/auth/me");
    setUser(result.user);
    await presentOAuthIfRequested();
  } catch {
    setUser(null);
    await presentOAuthIfRequested();
  }
}

async function presentOAuthIfRequested() {
  const params = new URLSearchParams(location.search);
  if (params.get("client_id") !== "idvb-desktop") return false;
  if (!state.user) {
    if (!authDialog.open) openAuth("login");
    return true;
  }
  document.querySelector("#oauth-account").textContent = `${state.user.displayName}（${state.user.email}）`;
  authDialog.close();
  if (!oauthDialog.open) oauthDialog.showModal();
  document.body.classList.add("dialog-open");
  return true;
}

async function allowOAuth() {
  const button = document.querySelector("#oauth-allow");
  button.disabled = true;
  const params = new URLSearchParams(location.search);
  try {
    const result = await api("/api/oauth/authorize", { method: "POST", body: JSON.stringify({
      clientId: params.get("client_id"), redirectUri: params.get("redirect_uri"),
      codeChallenge: params.get("code_challenge"), state: params.get("state"),
    }) });
    location.href = result.redirectUrl;
  } catch (error) {
    document.querySelector("#oauth-message").textContent = error.message;
    button.disabled = false;
  }
}

function denyOAuth() {
  const params = new URLSearchParams(location.search);
  const callback = new URL(params.get("redirect_uri"));
  callback.searchParams.set("error", "access_denied");
  callback.searchParams.set("state", params.get("state") || "");
  location.href = callback;
}

async function logout() {
  const button = document.querySelector("#logout-button");
  button.disabled = true;
  try {
    await api("/api/auth/logout", { method: "POST", body: "{}" });
    setUser(null);
    profileDialog.close();
  } finally {
    button.disabled = false;
  }
}

function setUser(user) {
  state.user = user;
  document.querySelectorAll(".signed-out-only").forEach((element) => element.hidden = Boolean(user));
  document.querySelectorAll(".signed-in-only").forEach((element) => element.hidden = !user);
  const canOpenConsole = Boolean(user && (user.isBuilder || user.isOfficial));
  document.querySelectorAll(".builder-only").forEach((element) => element.hidden = !canOpenConsole);
  if (!user) {
    closeBuilderConsole();
    return;
  }
  const initial = user.displayName.trim().slice(0, 1).toUpperCase();
  renderAvatar(document.querySelector("#header-avatar"), user.avatarUrl, initial);
  document.querySelector("#header-name").textContent = user.displayName;
  document.querySelector("#header-official").hidden = !user.isOfficial;
  document.querySelector("#header-builder").hidden = !user.isBuilder;
  renderAvatar(document.querySelector("#profile-avatar"), user.avatarUrl, initial);
  document.querySelector("#profile-name").textContent = user.displayName;
  document.querySelector("#profile-official").hidden = !user.isOfficial;
  document.querySelector("#profile-builder").hidden = !user.isBuilder;
  document.querySelector("#profile-email").textContent = user.email;

  const params = new URLSearchParams(location.search);
  if (canOpenConsole && params.get("view") === "builder") {
    openBuilderConsole();
  }
}

function renderAvatar(element, url, fallback) {
  element.textContent = url ? "" : fallback;
  element.style.backgroundImage = url ? `url("${encodeURI(url)}")` : "";
}

async function uploadAvatar(event) {
  const file = event.target.files[0];
  if (!file) return;
  const message = document.querySelector("#avatar-message");
  if (file.size > 2 * 1024 * 1024) { message.textContent = "图片不能超过 2 MB。"; return; }
  message.textContent = "正在上传…";
  try {
    const form = new FormData();
    form.set("avatar", file);
    const result = await api("/api/auth/avatar", { method: "POST", body: form });
    setUser(result.user);
    message.textContent = "头像已更新，IDVB 登录账户也会同步。";
  } catch (error) { message.textContent = error.message; }
  finally { event.target.value = ""; }
}

function openProfile() {
  if (!state.user) return openAuth("login");
  profileDialog.showModal();
  document.body.classList.add("dialog-open");
}

function closeOnBackdrop(event) {
  if (event.target === event.currentTarget) event.currentTarget.close();
}

function syncDialogState() {
  if (!authDialog.open && !profileDialog.open) document.body.classList.remove("dialog-open");
}

function clearErrors() {
  formMessage.textContent = "";
  formMessage.className = "form-message";
  authForm.querySelectorAll("[aria-invalid]").forEach((input) => input.removeAttribute("aria-invalid"));
  authForm.querySelectorAll("[data-error-for]").forEach((target) => target.textContent = "");
}

async function api(path, options = {}) {
  const headers = new Headers(options.headers);
  if (!(options.body instanceof FormData) && !headers.has("content-type"))
    headers.set("content-type", "application/json");
  const response = await fetch(path, {
    credentials: "same-origin",
    ...options,
    headers,
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) {
    const error = new Error(body.message || "请求失败，请稍后重试。");
    error.code = body.error;
    error.fields = body.fields;
    throw error;
  }
  return body;
}

let currentBuilderTab = "users";
let builderUsersData = [];
let builderFeedbacksData = [];
let builderMapsData = [];
let builderAnnouncementsData = [];

function openBuilderConsole() {
  if (!state.user || (!state.user.isBuilder && !state.user.isOfficial)) return;
  document.querySelector("#main").hidden = true;
  document.querySelector("#builder-console-section").hidden = false;
  switchBuilderTab(currentBuilderTab);
}

function closeBuilderConsole() {
  document.querySelector("#builder-console-section").hidden = true;
  document.querySelector("#main").hidden = false;
}

function switchBuilderTab(tab) {
  currentBuilderTab = tab;
  document.querySelectorAll(".builder-nav-item").forEach((btn) => {
    btn.classList.toggle("active", btn.dataset.tab === tab);
  });
  document.querySelectorAll(".builder-tab-panel").forEach((panel) => {
    panel.hidden = panel.id !== `panel-builder-${tab}`;
  });
  if (tab === "users") loadBuilderUsers();
  else if (tab === "feedbacks") loadBuilderFeedbacks();
  else if (tab === "maps") loadBuilderMaps();
  else if (tab === "announcements") loadBuilderAnnouncements();
  else if (tab === "versions") loadBuilderVersions();
}

async function loadBuilderVersions() {
  const status = document.querySelector("#version-policy-status");
  const tbody = document.querySelector("#versions-tbody");
  tbody.replaceChildren();
  status.textContent = "正在读取版本策略…";
  try {
    const { versions } = await api("/api/builder/versions");
    for (const policy of versions) {
      const row = document.createElement("tr");
      for (const value of [policy.version, policy.enabled ? "可用" : "已停用", policy.message, policy.updated_at]) {
        const cell = document.createElement("td"); cell.textContent = value; row.append(cell);
      }
      const cell = document.createElement("td");
      const edit = document.createElement("button"); edit.type = "button"; edit.textContent = "编辑";
      edit.addEventListener("click", () => {
        document.querySelector("#version-policy-version").value = policy.version;
        document.querySelector("#version-policy-enabled").checked = Boolean(policy.enabled);
        document.querySelector("#version-policy-message").value = policy.message;
      });
      cell.append(edit); row.append(cell); tbody.append(row);
    }
    status.textContent = "策略已加载。停用将在客户端下一次联网校验后生效（正常轮询间隔 60 秒）。";
  } catch (error) { status.textContent = error.message; }
}

document.querySelector("#version-policy-form").addEventListener("submit", async event => {
  event.preventDefault();
  const button = event.submitter;
  const version = document.querySelector("#version-policy-version").value.trim();
  const enabled = document.querySelector("#version-policy-enabled").checked;
  if (!enabled && !confirm(`停用 ${version} 后，普通账户必须升级到可用版本。确认停用？`)) return;
  button.disabled = true;
  try {
    await api("/api/builder/versions", { method: "PUT", body: JSON.stringify({ version, enabled,
      message: document.querySelector("#version-policy-message").value.trim() }) });
    await loadBuilderVersions();
  } catch (error) { document.querySelector("#version-policy-status").textContent = error.message; }
  finally { button.disabled = false; }
});

async function loadBuilderUsers() {
  const tbody = document.querySelector("#users-tbody");
  tbody.innerHTML = '<tr><td colspan="6" class="loading-td">正在加载用户数据...</td></tr>';
  try {
    const { users = [] } = await api("/api/builder/users");
    builderUsersData = users;
    updateUserStats();
    renderBuilderUsers();
  } catch (error) {
    tbody.innerHTML = `<tr><td colspan="6" class="empty-td" style="color: #dc2626;">加载用户失败: ${escapeHtml(error.message)}</td></tr>`;
  }
}

function updateUserStats() {
  document.querySelector("#stat-total-users").textContent = builderUsersData.length;
  document.querySelector("#stat-certified-users").textContent = builderUsersData.filter((u) => u.isOfficial).length;
  document.querySelector("#stat-builder-users").textContent = builderUsersData.filter((u) => u.isBuilder).length;
}

function renderBuilderUsers() {
  const tbody = document.querySelector("#users-tbody");
  const query = (document.querySelector("#user-search-input")?.value || "").trim().toLowerCase();
  const filtered = builderUsersData.filter((u) => {
    return !query || (u.id && u.id.toLowerCase().includes(query)) ||
      (u.displayName && u.displayName.toLowerCase().includes(query)) ||
      (u.email && u.email.toLowerCase().includes(query));
  });

  if (filtered.length === 0) {
    tbody.innerHTML = '<tr><td colspan="6" class="empty-td">没有找到符合条件的用户</td></tr>';
    return;
  }

  tbody.innerHTML = filtered.map((u) => {
    const dateStr = u.createdAt ? new Date(u.createdAt).toLocaleDateString("zh-CN") : "-";
    let certBadge = '<span class="cert-tag normal">普通成员</span>';
    if (u.isBuilder) {
      certBadge = '<span class="cert-tag builder"><svg viewBox="0 0 18 18" width="12" height="12" fill="currentColor" aria-hidden="true" style="margin-right:2px;"><path d="M2 1h13v4h-5v12H6V5H2zm12 1h3v4h-4V5h1z"/></svg>建设者</span>';
    } else if (u.isOfficial) {
      certBadge = '<span class="cert-tag official">✓ 已认证</span>';
    }

    let actionHtml = '<span class="no-attach" style="color:#b45309;">不可变更</span>';
    if (!u.isBuilder && !u.isOfficial) {
      actionHtml = `<button class="action-btn primary" data-certify-id="${escapeHtml(u.id)}">设为已认证</button>`;
    } else if (!u.isBuilder && u.isOfficial) {
      actionHtml = '<span class="no-attach" style="color:#0284c7;">✓ 已是认证用户</span>';
    }

    return `<tr>
      <td><code title="${escapeHtml(u.id)}">${escapeHtml(u.id.slice(0, 8))}...</code></td>
      <td><strong>${escapeHtml(u.displayName)}</strong></td>
      <td>${escapeHtml(u.email)}</td>
      <td>${certBadge}</td>
      <td>${dateStr}</td>
      <td>${actionHtml}</td>
    </tr>`;
  }).join("");

  tbody.querySelectorAll("[data-certify-id]").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const userId = btn.dataset.certifyId;
      btn.disabled = true;
      btn.textContent = "处理中...";
      try {
        const result = await api(`/api/builder/users/${userId}/certify`, {
          method: "POST",
          body: JSON.stringify({ isOfficial: true }),
        });
        const target = builderUsersData.find((u) => u.id === userId);
        if (target) Object.assign(target, result.user);
        updateUserStats();
        renderBuilderUsers();
      } catch (err) {
        alert("认证失败: " + err.message);
        btn.disabled = false;
        btn.textContent = "设为已认证";
      }
    });
  });
}

async function loadBuilderFeedbacks() {
  const container = document.querySelector("#feedbacks-list");
  container.innerHTML = '<p class="loading-state">正在加载反馈数据...</p>';
  try {
    const { feedbacks = [] } = await api("/api/builder/feedbacks");
    builderFeedbacksData = feedbacks;
    updateFeedbackStats();
    renderBuilderFeedbacks();
  } catch (error) {
    container.innerHTML = `<p class="empty-state" style="color: #dc2626;">加载反馈失败: ${escapeHtml(error.message)}</p>`;
  }
}

function updateFeedbackStats() {
  document.querySelector("#stat-total-feedbacks").textContent = builderFeedbacksData.length;
  document.querySelector("#stat-logs-feedbacks").textContent = builderFeedbacksData.filter((f) => f.has_logs === 1).length;
  document.querySelector("#stat-diags-feedbacks").textContent = builderFeedbacksData.filter((f) => f.has_diagnostics === 1).length;
}

function renderBuilderFeedbacks() {
  const container = document.querySelector("#feedbacks-list");
  if (builderFeedbacksData.length === 0) {
    container.innerHTML = '<p class="empty-state">暂无用户反馈。</p>';
    return;
  }

  container.innerHTML = builderFeedbacksData.map((f) => {
    const timeStr = f.created_at ? new Date(f.created_at).toLocaleString("zh-CN") : "-";
    const userDisplay = f.user_name ? `${escapeHtml(f.user_name)} (${escapeHtml(f.user_email || "")})` : escapeHtml(f.user_email || (f.user_id == null ? (f.contact_qq ? "桌面端未登录反馈" : "Android 匿名反馈") : "未知用户"));
    const safeId = escapeHtml(f.id);
    const shortId = escapeHtml(f.id.slice(0, 8));

    const actions = [];
    if (f.has_logs) {
      actions.push(`<a class="download-btn" href="/api/builder/feedbacks/${safeId}/download/logs" data-download-feedback="${safeId}" data-download-type="logs" download="logs-${shortId}.zip">下载日志 (${formatBytes(f.logs_size)})</a>`);
    }
    if (f.has_diagnostics) {
      actions.push(`<a class="download-btn secondary" href="/api/builder/feedbacks/${safeId}/download/diagnostics" data-download-feedback="${safeId}" data-download-type="diagnostics" download="diagnostics-${shortId}.zip">下载诊断数据 (${formatBytes(f.diagnostics_size)})</a>`);
    }
    const actionsHtml = actions.length > 0 ? actions.join("") : '<span class="no-attach">未附带排查包</span>';

    return `<div class="feedback-card">
      <div class="feedback-card-header">
        <div class="feedback-user-info">
          <strong>${userDisplay}</strong>
          ${f.contact_qq ? `<span>QQ号：${escapeHtml(f.contact_qq)}</span>` : ""}
          <code title="${safeId}">${shortId}</code>
        </div>
        <div class="feedback-time">${timeStr}</div>
      </div>
      <div class="feedback-desc">${escapeHtml(f.description)}</div>
      <div class="feedback-meta">
        <div>版本: <code>${escapeHtml(f.client_version || "未知")}</code> | IP: <code>${escapeHtml(f.client_ip || "未知")}</code></div>
        <div class="feedback-actions">${actionsHtml}</div>
      </div>
    </div>`;
  }).join("");
}

async function handleFeedbackActionClick(event) {
  const btn = event.target.closest("[data-download-feedback]");
  if (!btn) return;
  event.preventDefault();
  const feedbackId = btn.dataset.downloadFeedback;
  const type = btn.dataset.downloadType;
  if (!feedbackId || !type) return;
  await downloadFeedbackAttachment(btn, feedbackId, type);
}

async function downloadFeedbackAttachment(btn, feedbackId, type) {
  const originalText = btn.textContent;
  btn.style.pointerEvents = "none";
  btn.textContent = "正在下载...";
  try {
    const response = await fetch(`/api/builder/feedbacks/${feedbackId}/download/${type}`, {
      credentials: "same-origin",
    });
    if (!response.ok) {
      const body = await response.json().catch(() => ({}));
      throw new Error(body.message || `下载失败 (HTTP ${response.status})`);
    }
    const blob = await response.blob();
    const blobUrl = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = blobUrl;
    const disposition = response.headers.get("content-disposition") || "";
    const filenameMatch = /filename\*?=['"]?(?:UTF-\d['"]*)?([^;\r\n"']*)['"]?/i.exec(disposition);
    a.download = filenameMatch?.[1] ? decodeURIComponent(filenameMatch[1]) : `${type}-${feedbackId.slice(0, 8)}.zip`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(blobUrl), 1000);
  } catch (error) {
    alert(`下载失败: ${error.message}`);
  } finally {
    btn.style.pointerEvents = "";
    btn.textContent = originalText;
  }
}

async function loadBuilderMaps() {
  const tbody = document.querySelector("#maps-tbody");
  tbody.innerHTML = '<tr><td colspan="7" class="loading-td">正在加载地图数据...</td></tr>';
  try {
    const { maps = [] } = await api("/api/builder/maps");
    builderMapsData = maps;
    updateMapStats();
    renderBuilderMaps();
  } catch (error) {
    tbody.innerHTML = `<tr><td colspan="7" class="empty-td" style="color: #dc2626;">加载地图失败: ${escapeHtml(error.message)}</td></tr>`;
  }
}

function updateMapStats() {
  document.querySelector("#stat-total-maps").textContent = builderMapsData.length;
  document.querySelector("#stat-visible-maps").textContent = builderMapsData.filter((m) => !m.isHidden).length;
  document.querySelector("#stat-hidden-maps").textContent = builderMapsData.filter((m) => m.isHidden).length;
}

function renderBuilderMaps() {
  const tbody = document.querySelector("#maps-tbody");
  const query = (document.querySelector("#map-search-input")?.value || "").trim().toLowerCase();
  const filtered = builderMapsData.filter((m) => {
    return !query || (m.name && m.name.toLowerCase().includes(query)) ||
      (m.publisherName && m.publisherName.toLowerCase().includes(query)) ||
      (m.version && m.version.toLowerCase().includes(query));
  });

  if (filtered.length === 0) {
    tbody.innerHTML = '<tr><td colspan="7" class="empty-td">没有找到符合条件的地图</td></tr>';
    return;
  }

  tbody.innerHTML = filtered.map((m) => {
    const dateStr = m.updatedAt ? new Date(m.updatedAt).toLocaleDateString("zh-CN") : "-";
    const statusBadge = m.isHidden
      ? '<span class="status-tag hidden">已隐藏</span>'
      : '<span class="status-tag visible">展示中</span>';

    const visBtnText = m.isHidden ? "取消隐藏" : "隐藏";
    const visBtnClass = m.isHidden ? "action-btn warning" : "action-btn";
    const nextHidden = !m.isHidden;

    const thumbHtml = m.coverUrl
      ? `<img class="map-thumb" src="${escapeHtml(m.coverUrl)}" alt="">`
      : '<div class="map-thumb-placeholder"><svg viewBox="0 0 20 20" width="16" height="16" fill="currentColor" style="color:var(--muted);" aria-hidden="true"><path fill-rule="evenodd" d="M12 1.586l-4 4v12.828l4-4V1.586zM3.707 3.293A1 1 0 002 4v10a1 1 0 00.553.894l4 2A1 1 0 008 16V3.172L3.707 3.293zm10.74 1.293L18 6.414V16a1 1 0 01-1.447.894l-4-2A1 1 0 0112 14V1.586l2.447 1.22a1 1 0 01.553.894v.886z" clip-rule="evenodd"/></svg></div>';

    return `<tr>
      <td>${thumbHtml}</td>
      <td><strong>${escapeHtml(m.name)}</strong></td>
      <td>${escapeHtml(m.publisherName)}${m.publisherEmail ? `<br><small style="color:var(--muted);">${escapeHtml(m.publisherEmail)}</small>` : ""}</td>
      <td><code>v${escapeHtml(m.version)}</code></td>
      <td>${statusBadge}</td>
      <td>${dateStr}</td>
      <td>
        <div class="action-group">
          <button class="${visBtnClass}" data-map-toggle-id="${escapeHtml(m.id)}" data-next-hidden="${nextHidden}">${visBtnText}</button>
          <button class="action-btn danger" data-map-delete-id="${escapeHtml(m.id)}" data-map-name="${escapeHtml(m.name)}">删除</button>
        </div>
      </td>
    </tr>`;
  }).join("");

  tbody.querySelectorAll("[data-map-toggle-id]").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const mapId = btn.dataset.mapToggleId;
      const hidden = btn.dataset.nextHidden === "true";
      btn.disabled = true;
      btn.textContent = "更新中...";
      try {
        await api(`/api/builder/maps/${mapId}/visibility`, {
          method: "POST",
          body: JSON.stringify({ hidden }),
        });
        const target = builderMapsData.find((item) => item.id === mapId);
        if (target) target.isHidden = hidden;
        updateMapStats();
        renderBuilderMaps();
      } catch (err) {
        alert("状态更新失败: " + err.message);
        renderBuilderMaps();
      }
    });
  });

  tbody.querySelectorAll("[data-map-delete-id]").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const mapId = btn.dataset.mapDeleteId;
      const mapName = btn.dataset.mapName;
      if (!confirm(`确定要彻底删除地图【${mapName}】吗？\n此操作将永久删除地图包和所有关联数据，不可恢复。`)) return;
      btn.disabled = true;
      btn.textContent = "删除中...";
      try {
        await api(`/api/builder/maps/${mapId}`, { method: "DELETE" });
        builderMapsData = builderMapsData.filter((item) => item.id !== mapId);
        updateMapStats();
        renderBuilderMaps();
      } catch (err) {
        alert("删除失败: " + err.message);
        btn.disabled = false;
        btn.textContent = "删除";
      }
    });
  });
}

function formatBytes(bytes) {
  if (!bytes) return "0 B";
  if (bytes >= 1048576) return `${(bytes / 1048576).toFixed(1)} MB`;
  return `${Math.ceil(bytes / 1024)} KB`;
}

function escapeHtml(text) {
  if (!text) return "";
  return String(text).replace(/[&<>"']/g, (m) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    '"': "&quot;",
    "'": "&#39;",
  }[m]));
}

async function loadBuilderAnnouncements() {
  const tbody = document.querySelector("#announcements-tbody");
  tbody.innerHTML = '<tr><td colspan="6" class="loading-td">正在加载公告数据...</td></tr>';
  try {
    const { announcements = [] } = await api("/api/builder/announcements");
    builderAnnouncementsData = announcements;
    updateAnnouncementStats();
    renderBuilderAnnouncements();
  } catch (error) {
    tbody.innerHTML = `<tr><td colspan="6" class="empty-td" style="color: #dc2626;">加载公告失败: ${escapeHtml(error.message)}</td></tr>`;
  }
}

function updateAnnouncementStats() {
  document.querySelector("#stat-total-announcements").textContent = builderAnnouncementsData.length;
  document.querySelector("#stat-published-announcements").textContent = builderAnnouncementsData.filter((a) => a.isPublished).length;
  document.querySelector("#stat-popup-announcements").textContent = builderAnnouncementsData.filter((a) => a.priority > 0).length;
  document.querySelector("#stat-pinned-announcements").textContent = builderAnnouncementsData.filter((a) => a.isPinned).length;
}

function renderBuilderAnnouncements() {
  const tbody = document.querySelector("#announcements-tbody");
  const query = (document.querySelector("#announcement-search-input")?.value || "").trim().toLowerCase();
  const categoryFilter = (document.querySelector("#announcement-category-filter")?.value || "").trim();

  const filtered = builderAnnouncementsData.filter((a) => {
    const matchesCat = !categoryFilter || a.category === categoryFilter;
    const matchesQuery = !query ||
      (a.title && a.title.toLowerCase().includes(query)) ||
      (a.tag && a.tag.toLowerCase().includes(query)) ||
      (a.content && a.content.toLowerCase().includes(query)) ||
      (a.authorName && a.authorName.toLowerCase().includes(query));
    return matchesCat && matchesQuery;
  });

  if (filtered.length === 0) {
    tbody.innerHTML = '<tr><td colspan="6" class="empty-td">没有找到符合条件的公告</td></tr>';
    return;
  }

  tbody.innerHTML = filtered.map((a) => {
    const timeStr = a.publishAt ? new Date(a.publishAt).toLocaleDateString("zh-CN") : "-";
    const categoryLabels = { update: "更新说明", tips: "冷知识/技巧", notice: "系统通知" };
    const categoryLabel = categoryLabels[a.category] || a.category;
    const catBadge = `<span class="category-tag ${escapeHtml(a.category)}">${escapeHtml(categoryLabel)}</span>`;
    const tagBadge = a.tag ? `<code style="font-size:11px;">${escapeHtml(a.tag)}</code>` : '<span style="color:var(--muted);">-</span>';

    const statusTags = [];
    if (a.isPublished) statusTags.push('<span class="status-tag visible">已发布</span>');
    else statusTags.push('<span class="status-tag draft">草稿/下架</span>');

    if (a.isPinned) statusTags.push('<span class="status-tag pinned">置顶</span>');
    if (a.priority > 0) statusTags.push('<span class="status-tag popup">弹窗</span>');

    const statusHtml = `<div style="display:flex;flex-wrap:wrap;gap:4px;">${statusTags.join("")}</div>`;

    const pubBtnText = a.isPublished ? "下架" : "发布";
    const pubBtnClass = a.isPublished ? "action-btn warning" : "action-btn primary";
    const nextPublished = !a.isPublished;

    return `<tr>
      <td>
        <strong>${escapeHtml(a.title)}</strong>
        ${a.summary ? `<div style="font-size:12px;color:var(--muted);margin-top:2px;">${escapeHtml(a.summary.slice(0, 50))}${a.summary.length > 50 ? "..." : ""}</div>` : ""}
      </td>
      <td>${catBadge}</td>
      <td>${tagBadge}</td>
      <td>${statusHtml}</td>
      <td>${timeStr}</td>
      <td>
        <div class="action-group">
          <button class="action-btn" data-announcement-edit-id="${escapeHtml(a.id)}">编辑</button>
          <button class="${pubBtnClass}" data-announcement-toggle-pub-id="${escapeHtml(a.id)}" data-next-pub="${nextPublished}">${pubBtnText}</button>
          <button class="action-btn danger" data-announcement-delete-id="${escapeHtml(a.id)}" data-announcement-title="${escapeHtml(a.title)}">删除</button>
        </div>
      </td>
    </tr>`;
  }).join("");

  tbody.querySelectorAll("[data-announcement-edit-id]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const id = btn.dataset.announcementEditId;
      const target = builderAnnouncementsData.find((item) => item.id === id);
      if (target) openAnnouncementDialog(target);
    });
  });

  tbody.querySelectorAll("[data-announcement-toggle-pub-id]").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const id = btn.dataset.announcementTogglePubId;
      const isPublished = btn.dataset.nextPub === "true";
      btn.disabled = true;
      btn.textContent = "处理中...";
      try {
        await api(`/api/announcements/${id}`, {
          method: "PUT",
          body: JSON.stringify({ isPublished }),
        });
        const target = builderAnnouncementsData.find((item) => item.id === id);
        if (target) target.isPublished = isPublished;
        updateAnnouncementStats();
        renderBuilderAnnouncements();
      } catch (err) {
        alert("更新状态失败: " + err.message);
        renderBuilderAnnouncements();
      }
    });
  });

  tbody.querySelectorAll("[data-announcement-delete-id]").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const id = btn.dataset.announcementDeleteId;
      const title = btn.dataset.announcementTitle;
      if (!confirm(`确定要彻底删除公告【${title}】吗？\n此操作不可恢复。`)) return;
      btn.disabled = true;
      btn.textContent = "删除中...";
      try {
        await api(`/api/announcements/${id}`, { method: "DELETE" });
        builderAnnouncementsData = builderAnnouncementsData.filter((item) => item.id !== id);
        updateAnnouncementStats();
        renderBuilderAnnouncements();
      } catch (err) {
        alert("删除失败: " + err.message);
        btn.disabled = false;
        btn.textContent = "删除";
      }
    });
  });
}

function openAnnouncementDialog(item = null) {
  const dialog = document.querySelector("#announcement-dialog");
  const form = document.querySelector("#announcement-form");
  const titleEl = document.querySelector("#announcement-dialog-title");
  const msgEl = document.querySelector("#announcement-form-message");
  msgEl.textContent = "";
  msgEl.className = "form-message";

  if (item) {
    titleEl.textContent = "编辑公告";
    document.querySelector("#announcement-edit-id").value = item.id;
    document.querySelector("#announcement-title").value = item.title || "";
    document.querySelector("#announcement-category").value = item.category || "update";
    document.querySelector("#announcement-tag").value = item.tag || "";
    document.querySelector("#announcement-summary").value = item.summary || "";
    document.querySelector("#announcement-content").value = item.content || "";
    document.querySelector("#announcement-cover").value = item.coverImageUrl || "";
    document.querySelector("#announcement-min-version").value = item.minClientVersion || "";
    document.querySelector("#announcement-published").checked = Boolean(item.isPublished);
    document.querySelector("#announcement-pinned").checked = Boolean(item.isPinned);
    document.querySelector("#announcement-popup").checked = item.priority > 0;
  } else {
    titleEl.textContent = "发布新公告";
    form.reset();
    document.querySelector("#announcement-edit-id").value = "";
    document.querySelector("#announcement-published").checked = true;
    document.querySelector("#announcement-pinned").checked = false;
    document.querySelector("#announcement-popup").checked = false;
  }

  dialog.showModal();
  document.body.classList.add("dialog-open");
}

async function submitAnnouncementForm(event) {
  event.preventDefault();
  const msgEl = document.querySelector("#announcement-form-message");
  const submitBtn = document.querySelector("#announcement-submit-btn");
  msgEl.textContent = "";
  msgEl.className = "form-message";

  const id = document.querySelector("#announcement-edit-id").value.trim();
  const title = document.querySelector("#announcement-title").value.trim();
  const category = document.querySelector("#announcement-category").value;
  const tag = document.querySelector("#announcement-tag").value.trim() || null;
  const summary = document.querySelector("#announcement-summary").value.trim() || null;
  const content = document.querySelector("#announcement-content").value.trim();
  const coverImageUrl = document.querySelector("#announcement-cover").value.trim() || null;
  const minClientVersion = document.querySelector("#announcement-min-version").value.trim() || null;
  const isPublished = document.querySelector("#announcement-published").checked;
  const isPinned = document.querySelector("#announcement-pinned").checked;
  const isPopup = document.querySelector("#announcement-popup").checked;
  const priority = isPopup ? 1 : 0;

  if (!title) {
    msgEl.textContent = "请填写公告标题。";
    return;
  }
  if (!content) {
    msgEl.textContent = "请填写公告正文内容。";
    return;
  }

  submitBtn.disabled = true;
  submitBtn.textContent = "正在保存...";

  const payload = {
    title,
    category,
    tag,
    summary,
    content,
    coverImageUrl,
    minClientVersion,
    isPublished,
    isPinned,
    priority,
  };

  try {
    if (id) {
      await api(`/api/announcements/${id}`, {
        method: "PUT",
        body: JSON.stringify(payload),
      });
      const target = builderAnnouncementsData.find((item) => item.id === id);
      if (target) Object.assign(target, payload, { updatedAt: new Date().toISOString() });
    } else {
      const result = await api("/api/announcements", {
        method: "POST",
        body: JSON.stringify(payload),
      });
      if (result.announcement) {
        builderAnnouncementsData.unshift(result.announcement);
      }
    }

    updateAnnouncementStats();
    renderBuilderAnnouncements();
    document.querySelector("#announcement-dialog").close();
  } catch (error) {
    msgEl.textContent = error.message;
  } finally {
    submitBtn.disabled = false;
    submitBtn.textContent = "保存公告";
  }
}

const ANNOUNCEMENT_TEMPLATES = {
  update: {
    title: "v[版本号] 版本更新说明",
    category: "update",
    tag: "版本更新",
    summary: "本次更新针对核心算法与交互体验进行了全面升级，优化了对齐性能与稳定性，推荐所有选手更新体验。",
    content: `# [版本号] 版本更新说明

> [!TIP]
> 推荐所有玩家与选手更新体验。本次更新针对核心算法与交互体验进行了全面升级。

## 核心新特性与优化

- **[特性一]**：详细说明该特性的作用、性能提升（如延迟降低、流畅度改善）与使用场景。
- **[特性二]**：介绍新增功能，附带操作路径与快捷入口。
- **[特性三]**：视觉或交互层面的改进。

## 问题修复与体验改进

| 模块 | 优化 / 修复内容 | 状态 |
| :--- | :--- | :--- |
| 对齐引擎 | 优化特征提取与残差收敛，杜绝极端场景漂移 | 已优化 |
| 界面交互 | 完善浅色与深色主题适配，提升高 DPI 下文字清晰度 | 已修复 |
| 网络同步 | 优化后台异步请求策略，彻底杜绝主线程阻塞 | 已提升 |

## 演示与预览

![功能演示图片或GIF](https://download.xgflee.com/assets/demo-placeholder.png)

## 升级与兼容说明

- 最低支持版本：v1.4.0
- 本次更新无需手动重置本地配置，安装后即可自动无缝热加载。`,
    minClientVersion: "1.4.0",
    isPublished: true,
    isPinned: true,
    isPopup: true,
  },
  tips: {
    title: "[地图名称/技巧主题] 实战冷知识",
    category: "tips",
    tag: "技巧与冷知识",
    summary: "分享关于[具体技巧/点位]的实用心得与操作细节，助力排位博弈。",
    content: `# [地图名称/技巧主题] 实战冷知识

> [!NOTE]
> 本篇冷知识适用于当前版本，可在实战对局中灵活运用。

## 技巧核心要点

1. **观察与预判**：
   - 关注小地图上的关键结构与地形特征。
   - 掌握窗口与板区的翻越判定距离。

2. **博弈细节**：
   - 细节说明一。
   - 细节说明二。

## 点位与路线演示

![路线示意图](https://download.xgflee.com/assets/tips-placeholder.png)

## 总结与建议

- 建议在自定义练习模式中多加体会距离感。`,
    minClientVersion: "",
    isPublished: true,
    isPinned: false,
    isPopup: false,
  },
  notice: {
    title: "[通知主题] 相关说明",
    category: "notice",
    tag: "系统通知",
    summary: "关于[事项]的最新通知与说明，请查阅。",
    content: `# [通知主题] 相关说明

> [!IMPORTANT]
> 请注意查阅以下通知内容。

## 通知详情

- **生效时间**：2026-09-20 起
- **涉及范围**：全部用户 / 指定功能
- **主要内容**：请在此填写具体通知说明。

如有疑问，欢迎通过客户端反馈或社区与我们联系。`,
    minClientVersion: "",
    isPublished: true,
    isPinned: false,
    isPopup: false,
  },
};

function applyAnnouncementTemplate(templateKey) {
  if (templateKey === "clear") {
    document.querySelector("#announcement-content").value = "";
    return;
  }

  const tpl = ANNOUNCEMENT_TEMPLATES[templateKey];
  if (!tpl) return;

  const currentContent = document.querySelector("#announcement-content").value.trim();
  if (currentContent && !confirm("应用模板将覆盖当前编辑框中的内容，确定要应用吗？")) {
    return;
  }

  document.querySelector("#announcement-title").value = tpl.title;
  document.querySelector("#announcement-category").value = tpl.category;
  document.querySelector("#announcement-tag").value = tpl.tag;
  document.querySelector("#announcement-summary").value = tpl.summary;
  document.querySelector("#announcement-content").value = tpl.content;
  document.querySelector("#announcement-min-version").value = tpl.minClientVersion;
  document.querySelector("#announcement-published").checked = tpl.isPublished;
  document.querySelector("#announcement-pinned").checked = tpl.isPinned;
  document.querySelector("#announcement-popup").checked = tpl.isPopup;
}


