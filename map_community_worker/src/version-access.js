// Only authenticated server records decide access; client role flags are ignored.
export const DOWNLOAD_URL = "https://download.xgflee.com/";

export function validProductVersion(value) {
  return typeof value === "string" && /^(0|[1-9]\d{0,3})\.(0|[1-9]\d{0,3})\.(0|[1-9]\d{0,3})$/.test(value);
}

export function accessDecision(user, policy) {
  const offlineExempt = user.is_official === 1 || user.is_builder === 1;
  return { allowed: offlineExempt || policy?.enabled === 1, offlineExempt };
}

export async function signAccess(env, claims) {
  const key = await crypto.subtle.importKey("pkcs8",
    Uint8Array.from(atob(env.VERSION_ACCESS_PRIVATE_KEY), c => c.charCodeAt(0)),
    { name: "ECDSA", namedCurve: "P-256" }, false, ["sign"]);
  const bytes = new TextEncoder().encode(JSON.stringify(claims));
  const signature = new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, bytes));
  return { payload: btoa(String.fromCharCode(...bytes)), signature: btoa(String.fromCharCode(...signature)) };
}

export async function versionAccessResponse(request, env, { requirePublishToken, json, ApiError, sha256Base64Url }) {
  const user = await requirePublishToken(request, env);
  const body = await request.json();
  if (!validProductVersion(body.version) || !/^[A-Za-z0-9_-]{43}$/.test(body.nonce || ""))
    throw new ApiError(400, "invalid_version", "版本校验参数无效。");
  const policy = await env.COMMUNITY_DB.prepare("SELECT * FROM software_versions WHERE version = ?1").bind(body.version).first();
  const decision = accessDecision(user, policy);
  const now = Math.floor(Date.now() / 1000);
  return json(await signAccess(env, {
    audience: "idvb-desktop-access-v1", version: body.version, nonce: body.nonce,
    tokenHash: await sha256Base64Url(request.headers.get("authorization").slice(7)),
    ...decision, issuedAt: now, expiresAt: now + 120,
    message: decision.allowed ? "" : (policy?.message || "当前版本已停止使用，请升级到可用版本。"),
    downloadUrl: DOWNLOAD_URL,
  }));
}

export async function manageVersions(request, env, { requireSessionUser, rejectCrossSite, json, ApiError }) {
  rejectCrossSite(request, new URL(request.url));
  const user = await requireSessionUser(request, env);
  if (user.is_builder !== 1) throw new ApiError(403, "builder_required", "只有建设者账户可以管理软件版本。");
  if (request.method === "GET") {
    const rows = await env.COMMUNITY_DB.prepare("SELECT * FROM software_versions ORDER BY updated_at DESC").all();
    return json({ versions: rows.results || [] });
  }
  const body = await request.json();
  if (!validProductVersion(body.version) || typeof body.enabled !== "boolean" ||
      typeof body.message !== "string" || body.message.length > 500)
    throw new ApiError(400, "invalid_policy", "请输入正确的产品版本、启用状态和不超过 500 字的说明。");
  const now = new Date().toISOString();
  // D1 batch is transactional: every policy write has a corresponding audit entry.
  await env.COMMUNITY_DB.batch([
    env.COMMUNITY_DB.prepare(`INSERT INTO software_versions (version, enabled, message, updated_at, updated_by)
      VALUES (?1, ?2, ?3, ?4, ?5) ON CONFLICT(version) DO UPDATE SET
      enabled=excluded.enabled, message=excluded.message, updated_at=excluded.updated_at, updated_by=excluded.updated_by`)
      .bind(body.version, body.enabled ? 1 : 0, body.message.trim(), now, user.id),
    env.COMMUNITY_DB.prepare("INSERT INTO software_version_audit (version, enabled, message, changed_at, changed_by) VALUES (?1, ?2, ?3, ?4, ?5)")
      .bind(body.version, body.enabled ? 1 : 0, body.message.trim(), now, user.id),
  ]);
  return json({ saved: true });
}
