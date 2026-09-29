import { serveIdvbWeb } from "./web.js";
import { versionAccessResponse, manageVersions } from "./version-access.js";

const SESSION_COOKIE = "idvb_community_session";
const SESSION_TTL_SECONDS = 60 * 60 * 24 * 30;
// ponytail: Workerd caps PBKDF2 at 100k; raise this when the runtime supports it.
const PASSWORD_ITERATIONS = 100_000;
const PUBLISH_TOKEN_TTL_SECONDS = 60 * 60 * 24 * 30;
const PASSWORD_RESET_TTL_SECONDS = 10 * 60;
const TURNSTILE_CLEARANCE_COOKIE = "idvb_turnstile_clearance";
const TURNSTILE_CLEARANCE_TTL_SECONDS = 10 * 60;
const JSON_HEADERS = {
  "content-type": "application/json; charset=utf-8",
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    const web = await serveIdvbWeb(request, env);
    if (web) return web;

    if (url.pathname.startsWith("/api/")) {
      return apiResponse(request, env, url);
    }

    if (request.method !== "GET" && request.method !== "HEAD") {
      return new Response("Method Not Allowed", { status: 405, headers: { allow: "GET, HEAD" } });
    }

    const asset = await env.ASSETS.fetch(request);
    if (url.pathname === "/" || url.pathname.endsWith(".html")) {
      const headers = new Headers(asset.headers);
      headers.set("cache-control", "no-store");
      return new Response(asset.body, { status: asset.status, statusText: asset.statusText, headers });
    }
    return asset;
  },
};

async function apiResponse(request, env, url) {
  if (!env.COMMUNITY_DB) {
    return json({ error: "service_unavailable", message: "社区数据库尚未配置。" }, 503);
  }

  const route = `${request.method} ${url.pathname}`;
  try {
    if (route === "POST /api/client/version-access")
      return await versionAccessResponse(request, env, { requirePublishToken, json, ApiError, sha256Base64Url });
    if (route === "GET /api/builder/versions" || route === "PUT /api/builder/versions")
      return await manageVersions(request, env, { requireSessionUser, rejectCrossSite, json, ApiError });
    if (route === "GET /api/maps") {
      return await mapCatalogResponse(env);
    }
    if (route === "POST /api/maps/publish") {
      await enforceRateLimit(env.PUBLISH_TOKEN_RATE_LIMITER, request);
      return await publishMapResponse(request, env);
    }
    if (route === "GET /api/auth/capabilities") {
      return json({ password: true, oauth: true, oauthAuthorizePath: "/oauth/authorize", turnstileSiteKey: env.TURNSTILE_SITE_KEY || null, turnstileRequired: !await hasTurnstileClearance(request, env) });
    }
    if (route === "GET /api/auth/me") {
      return await currentUserResponse(request, env);
    }
    if (route === "POST /api/auth/avatar") {
      rejectCrossSite(request, url);
      return await updateAvatarResponse(request, env, url);
    }
    if (route === "POST /api/auth/register") {
      rejectCrossSite(request, url);
      await enforceRateLimit(env.REGISTER_RATE_LIMITER, request);
      return await withTurnstileClearance(await registerResponse(request, env), request, env);
    }
    if (route === "POST /api/auth/login") {
      rejectCrossSite(request, url);
      await enforceRateLimit(env.LOGIN_RATE_LIMITER, request);
      return await withTurnstileClearance(await loginResponse(request, env), request, env);
    }
    if (route === "POST /api/auth/password-reset/request") {
      rejectCrossSite(request, url);
      await enforceRateLimit(env.PASSWORD_RESET_RATE_LIMITER, request);
      return await withTurnstileClearance(await requestPasswordResetResponse(request, env), request, env);
    }
    if (route === "POST /api/auth/password-reset/confirm") {
      rejectCrossSite(request, url);
      await enforceRateLimit(env.PASSWORD_RESET_RATE_LIMITER, request);
      return await withTurnstileClearance(await confirmPasswordResetResponse(request, env), request, env);
    }
    if (route === "POST /api/auth/logout") {
      rejectCrossSite(request, url);
      return await logoutResponse(request, env);
    }
    if (route === "POST /api/auth/publish-token") {
      rejectCrossSite(request, url);
      await enforceRateLimit(env.PUBLISH_TOKEN_RATE_LIMITER, request);
      return await publishTokenResponse(request, env);
    }
    if (route === "GET /api/auth/publish-token") {
      return await validatePublishTokenResponse(request, env);
    }
    if (route === "DELETE /api/auth/publish-token") {
      return await revokePublishTokenResponse(request, env);
    }
    const subscriptionMatch = /^GET \/api\/maps\/subscriptions\/([0-9a-f-]{36})\/(feed\.json|[^/]+\.idvm\.secure)$/.exec(route);
    if (subscriptionMatch) return await subscriptionObjectResponse(env.MAP_BUCKET, subscriptionMatch[1], subscriptionMatch[2]);
    const coverMatch = /^GET \/api\/maps\/covers\/([0-9a-f-]{36})$/.exec(route);
    if (coverMatch) return await subscriptionObjectResponse(env.MAP_BUCKET, coverMatch[1], "cover");
    const avatarMatch = /^GET \/api\/avatars\/([0-9a-f-]{36})$/.exec(route);
    if (avatarMatch) return await avatarObjectResponse(env.MAP_BUCKET, avatarMatch[1]);
    if (route === "POST /api/oauth/authorize") {
      rejectCrossSite(request, url);
      return await oauthAuthorizeResponse(request, env);
    }
    if (route === "POST /api/oauth/token") {
      await enforceRateLimit(env.PUBLISH_TOKEN_RATE_LIMITER, request);
      return await oauthTokenResponse(request, env);
    }
    if (route === "POST /api/feedback" || route === "POST /api/android/feedback") {
      rejectCrossSite(request, url);
      if (!env.FEEDBACK_RATE_LIMITER) {
        throw new ApiError(503, "service_unavailable", "反馈服务暂不可用，请稍后重试。");
      }
      await enforceRateLimit(env.FEEDBACK_RATE_LIMITER, request);
      return await submitFeedbackResponse(request, env, route === "POST /api/android/feedback");
    }
    if (route === "GET /api/builder/users") {
      return await builderUsersResponse(request, env);
    }
    const certifyMatch = /^POST \/api\/builder\/users\/([0-9a-f-]{36})\/certify$/.exec(route);
    if (certifyMatch) {
      return await certifyUserResponse(request, env, certifyMatch[1]);
    }
    if (route === "GET /api/builder/feedbacks") {
      return await builderFeedbacksResponse(request, env);
    }
    const feedbackDownloadMatch = /^GET \/api\/builder\/feedbacks\/([0-9a-f-]{36})\/download\/(logs|diagnostics)$/.exec(route);
    if (feedbackDownloadMatch) {
      return await builderFeedbackDownloadResponse(request, env, feedbackDownloadMatch[1], feedbackDownloadMatch[2]);
    }
    if (route === "GET /api/builder/maps") {
      return await builderMapsResponse(request, env);
    }
    const mapVisibilityMatch = /^POST \/api\/builder\/maps\/([0-9a-f-]{36})\/visibility$/.exec(route);
    if (mapVisibilityMatch) {
      return await builderMapVisibilityResponse(request, env, mapVisibilityMatch[1]);
    }
    const mapDeleteMatch = /^DELETE \/api\/builder\/maps\/([0-9a-f-]{36})$/.exec(route);
    if (mapDeleteMatch) {
      return await builderMapDeleteResponse(request, env, mapDeleteMatch[1]);
    }
    if (route === "GET /api/builder/announcements") {
      return await builderAnnouncementsResponse(request, env);
    }
    const announcementDetailMatch = /^GET \/api\/announcements\/([0-9a-f-]{36})$/.exec(route);
    if (announcementDetailMatch) {
      return await announcementDetailResponse(env, announcementDetailMatch[1]);
    }
    if (route === "GET /api/announcements") {
      return await announcementsListResponse(request, env, url);
    }
    if (route === "POST /api/announcements") {
      rejectCrossSite(request, url);
      return await createAnnouncementResponse(request, env);
    }
    const announcementUpdateMatch = /^PUT \/api\/announcements\/([0-9a-f-]{36})$/.exec(route);
    if (announcementUpdateMatch) {
      rejectCrossSite(request, url);
      return await updateAnnouncementResponse(request, env, announcementUpdateMatch[1]);
    }
    const announcementDeleteMatch = /^DELETE \/api\/announcements\/([0-9a-f-]{36})$/.exec(route);
    if (announcementDeleteMatch) {
      rejectCrossSite(request, url);
      return await deleteAnnouncementResponse(request, env, announcementDeleteMatch[1]);
    }
    return json({ error: "not_found", message: "接口不存在。" }, 404);
  } catch (error) {
    if (error instanceof ApiError) {
      return json({ error: error.code, message: error.message, fields: error.fields }, error.status);
    }
    console.error("Community API failure", error);
    return json({ error: "internal_error", message: "服务暂时不可用，请稍后重试。" }, 500);
  }
}

async function publishMapResponse(request, env) {
  const user = await requirePublishToken(request, env);
  const form = await publicationFormData(request);
  const feed = form.get("feed");
  const packageFile = form.get("package");
  const contentKey = String(form.get("contentKey") || "");
  const name = String(form.get("name") || "").trim();
  const cover = form.get("cover");
  if (!(feed instanceof File) || !(packageFile instanceof File) || packageFile.size > 90 * 1024 * 1024)
    throw new ApiError(400, "invalid_publication", "地图发布文件缺失或超过 90 MB。");
  if (!/^[A-Za-z0-9_-]{43}$/.test(contentKey))
    throw new ApiError(400, "invalid_publication", "订阅内容密钥无效。");
  if (!name || name.length > 80 || !(cover instanceof File) || cover.size > 5 * 1024 * 1024
      || !["image/png", "image/jpeg"].includes(cover.type))
    throw new ApiError(400, "invalid_publication", "地图包名称或封面无效。");
  const feedBytes = new Uint8Array(await feed.arrayBuffer());
  const packageBytes = new Uint8Array(await packageFile.arrayBuffer());
  if (feedBytes.length > 2 * 1024 * 1024)
    throw new ApiError(400, "invalid_publication", "地图订阅 feed 过大。");
  const envelope = JSON.parse(new TextDecoder().decode(feedBytes));
  const payloadBytes = base64ToBytes(envelope.payload || "");
  const payload = JSON.parse(new TextDecoder().decode(payloadBytes));
  const publicKey = await importPublisherKey(envelope.publisherPublicKeyPem || "");
  if (!await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, publicKey,
    base64ToBytes(envelope.signature || ""), payloadBytes))
    throw new ApiError(400, "invalid_signature", "地图订阅签名无效。");
  const keyId = await publisherKeyId(envelope.publisherPublicKeyPem);
  const packageHash = bytesToHex(new Uint8Array(await crypto.subtle.digest("SHA-256", packageBytes)));
  if (payload.schemaVersion !== 1 || payload.publisherKeyId !== keyId
      || payload.publisherDisplayName !== user.display_name
      || Boolean(payload.isOfficialPublisher) !== Boolean(user.is_official)
      || Boolean(payload.isBuilderPublisher) !== Boolean(user.is_builder)
      || payload.encryptedLength !== packageBytes.length
      || String(payload.encryptedSha256 || "").toUpperCase() !== packageHash
      || !/^[0-9a-f-]{36}$/i.test(payload.publicationId || "")
      || !/^[A-Za-z0-9._-]+\.idvm\.secure$/.test(payload.packageUri || ""))
    throw new ApiError(400, "invalid_publication", "地图发布内容与账户、签名或哈希不一致。");

  const publicationId = payload.publicationId.toLowerCase();
  const existing = await env.COMMUNITY_DB.prepare(
    "SELECT user_id, content_key FROM map_publications WHERE id = ?1",
  ).bind(publicationId).first();
  if (existing && existing.user_id !== user.id)
    throw new ApiError(403, "publication_owner_required", "只有原发布者能够更新这个地图包。");
  if (existing && existing.content_key !== contentKey)
    throw new ApiError(400, "content_key_changed", "更新地图包时不能更换订阅内容密钥。");
  const packageKey = `community/${publicationId}/${payload.packageUri}`;
  const feedKey = `community/${publicationId}/feed.json`;
  const coverKey = `community/${publicationId}/cover`;
  await env.MAP_BUCKET.put(packageKey, packageBytes, { httpMetadata: { contentType: "application/octet-stream" } });
  await env.MAP_BUCKET.put(coverKey, new Uint8Array(await cover.arrayBuffer()), { httpMetadata: { contentType: cover.type } });
  await env.MAP_BUCKET.put(feedKey, feedBytes, { httpMetadata: { contentType: "application/json" } });
  const now = new Date().toISOString();
  if (existing) {
    await env.COMMUNITY_DB.prepare(
      `UPDATE map_publications SET display_name=?2, name=?3, version=?4, feed_key=?5,
         package_key=?6, cover_key=?7, publisher_key_id=?8, updated_at=?9 WHERE id=?1 AND user_id=?10`,
    ).bind(publicationId, user.display_name, name, payload.version, feedKey, packageKey,
      coverKey, keyId, now, user.id).run();
  } else {
    await env.COMMUNITY_DB.prepare(
      `INSERT INTO map_publications
        (id, display_name, name, version, feed_key, package_key, cover_key, publisher_key_id,
         created_at, updated_at, user_id, content_key)
       VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?9, ?10, ?11)`,
    ).bind(publicationId, user.display_name, name, payload.version, feedKey, packageKey,
      coverKey, keyId, now, user.id, contentKey).run();
  }
  return json({
    publicationId,
    publisherName: user.display_name,
    subscriptionLink: subscriptionLink(request.url, publicationId, contentKey, keyId),
  }, 201);
}

async function publicationFormData(request) {
  const contentType = request.headers.get("content-type") || "";
  const boundary = /boundary=(?:"([^"]+)"|([^;]+))/i.exec(contentType)?.slice(1).find(Boolean)?.trim();
  if (!boundary) throw new ApiError(400, "invalid_publication", "地图发布请求格式无效。");
  const bytes = new Uint8Array(await request.arrayBuffer());
  const marker = new TextEncoder().encode(`--${boundary}`);
  const parts = new Map();
  let cursor = indexOfBytes(bytes, marker, 0);
  while (cursor >= 0) {
    const headerStart = cursor + marker.length + 2;
    const headerEnd = indexOfBytes(bytes, new Uint8Array([13, 10, 13, 10]), headerStart);
    if (headerEnd < 0) break;
    const headers = new TextDecoder().decode(bytes.subarray(headerStart, headerEnd));
    const disposition = /^content-disposition:\s*form-data;([^\r\n]*)/im.exec(headers)?.[1] || "";
    const name = /(?:^|;)\s*name=(?:"([^"]+)"|([^;\s]+))/i.exec(disposition)?.slice(1).find(Boolean);
    if (!name) throw new ApiError(400, "invalid_publication", "地图发布字段名称无效。");
    const filename = /(?:^|;)\s*filename=(?:"([^"]*)"|([^;\s]+))/i.exec(disposition)?.slice(1).find(Boolean);
    const dataStart = headerEnd + 4;
    const nextMarker = indexOfBytes(bytes, marker, dataStart);
    if (nextMarker < 2) break;
    const data = bytes.subarray(dataStart, nextMarker - 2);
    const type = /^content-type:\s*([^\r\n]+)/im.exec(headers)?.[1]?.trim();
    parts.set(name, filename === undefined
      ? new TextDecoder().decode(data)
      : new File([data], filename, { type }));
    cursor = nextMarker;
  }
  return { get: (name) => parts.get(name) ?? null };
}

function indexOfBytes(haystack, needle, start) {
  outer: for (let index = start; index <= haystack.length - needle.length; index += 1) {
    for (let offset = 0; offset < needle.length; offset += 1)
      if (haystack[index + offset] !== needle[offset]) continue outer;
    return index;
  }
  return -1;
}

async function subscriptionObjectResponse(bucket, publicationId, filename) {
  const key = `community/${publicationId}/${filename}`;
  const object = await bucket.get(key);
  if (!object) throw new ApiError(404, "not_found", "地图订阅文件不存在。");
  const headers = new Headers({ "cache-control": filename === "feed.json" || filename === "cover" ? "no-store" : "public, max-age=31536000, immutable" });
  object.writeHttpMetadata(headers);
  headers.set("content-length", String(object.size));
  headers.set("x-content-type-options", "nosniff");
  return new Response(object.body, { headers });
}

async function avatarObjectResponse(bucket, userId) {
  const object = await bucket.get(`community/avatars/${userId}`);
  if (!object) throw new ApiError(404, "not_found", "头像不存在。");
  const headers = new Headers({ "cache-control": "public, max-age=31536000, immutable" });
  object.writeHttpMetadata(headers);
  headers.set("content-length", String(object.size));
  headers.set("x-content-type-options", "nosniff");
  return new Response(object.body, { headers });
}

function subscriptionLink(requestUrl, id, contentKey, publisherKeyId) {
  const feed = new URL(`/api/maps/subscriptions/${id}/feed.json`, requestUrl).toString();
  return `idvb-sub://v1?feed=${encodeURIComponent(feed)}&key=${contentKey}&publisher=${publisherKeyId}`;
}

async function revokePublishTokenResponse(request, env) {
  const authorization = request.headers.get("authorization") || "";
  if (authorization.startsWith("Bearer ")) {
    await env.COMMUNITY_DB.prepare("DELETE FROM publish_tokens WHERE token_hash = ?1")
      .bind(await sha256Base64Url(authorization.slice(7))).run();
  }
  return json({ revoked: true });
}

async function oauthAuthorizeResponse(request, env) {
  const user = await requireSessionUser(request, env);
  const body = await readJson(request);
  if (body.clientId !== "idvb-desktop" || !/^http:\/\/127\.0\.0\.1:\d+\/callback$/.test(body.redirectUri || ""))
    throw new ApiError(400, "invalid_oauth_request", "登录请求无效，请返回 IDVB 后重试。");
  if (!/^[A-Za-z0-9_-]{43}$/.test(body.codeChallenge || "") || !/^[A-Za-z0-9_-]{32,128}$/.test(body.state || ""))
    throw new ApiError(400, "invalid_oauth_request", "登录请求无效，请返回 IDVB 后重试。");

  const code = randomBase64Url(32);
  const now = new Date();
  await env.COMMUNITY_DB.prepare(
    `INSERT INTO oauth_authorization_codes
      (code_hash, user_id, client_id, redirect_uri, code_challenge, created_at, expires_at)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)`,
  ).bind(await sha256Base64Url(code), user.id, body.clientId, body.redirectUri,
    body.codeChallenge, now.toISOString(), new Date(now.getTime() + 120_000).toISOString()).run();
  const callback = new URL(body.redirectUri);
  callback.searchParams.set("code", code);
  callback.searchParams.set("state", body.state);
  return json({ redirectUrl: callback.toString() });
}

async function oauthTokenResponse(request, env) {
  const body = await readJson(request);
  if (body.clientId !== "idvb-desktop" || typeof body.code !== "string" || typeof body.codeVerifier !== "string")
    throw new ApiError(400, "invalid_oauth_request", "登录请求无效，请返回 IDVB 后重试。");
  const codeHash = await sha256Base64Url(body.code);
  const record = await env.COMMUNITY_DB.prepare(
    `SELECT oauth_authorization_codes.*, users.id AS id, users.email, users.display_name,
            users.publisher_handle, users.avatar_url, users.is_official, users.is_builder,
            users.email_verified_at, users.created_at
       FROM oauth_authorization_codes JOIN users ON users.id = oauth_authorization_codes.user_id
      WHERE code_hash = ?1 AND expires_at > ?2`,
  ).bind(codeHash, new Date().toISOString()).first();
  await env.COMMUNITY_DB.prepare("DELETE FROM oauth_authorization_codes WHERE code_hash = ?1").bind(codeHash).run();
  if (!record || record.client_id !== body.clientId || record.redirect_uri !== body.redirectUri
      || await sha256Base64Url(body.codeVerifier) !== record.code_challenge)
    throw new ApiError(401, "invalid_authorization_code", "登录已过期，请返回 IDVB 后重试。");
  return await issuePublishToken(env, record);
}

async function publishTokenResponse(request, env) {
  const user = await requireSessionUser(request, env);
  return await issuePublishToken(env, user);
}

async function issuePublishToken(env, user) {
  const token = randomBase64Url(32);
  const tokenHash = await sha256Base64Url(token);
  const createdAt = new Date();
  const expiresAt = new Date(createdAt.getTime() + PUBLISH_TOKEN_TTL_SECONDS * 1000);
  await env.COMMUNITY_DB.prepare(
    `INSERT INTO publish_tokens (token_hash, user_id, created_at, expires_at)
     VALUES (?1, ?2, ?3, ?4)`,
  ).bind(tokenHash, user.id, createdAt.toISOString(), expiresAt.toISOString()).run();
  return json({ token, expiresAt: expiresAt.toISOString(), user: publicUser(user) });
}

async function validatePublishTokenResponse(request, env) {
  const authorization = request.headers.get("authorization") || "";
  if (!authorization.startsWith("Bearer ")) {
    throw new ApiError(401, "publish_token_required", "请先登录。");
  }
  const tokenHash = await sha256Base64Url(authorization.slice(7));
  const record = await env.COMMUNITY_DB.prepare(
    `SELECT users.id, users.email, users.display_name, users.publisher_handle, users.avatar_url,
            users.is_official, users.is_builder, users.email_verified_at, users.created_at,
            publish_tokens.expires_at
       FROM publish_tokens JOIN users ON users.id = publish_tokens.user_id
      WHERE publish_tokens.token_hash = ?1 AND publish_tokens.expires_at > ?2`,
  ).bind(tokenHash, new Date().toISOString()).first();
  if (!record) {
    throw new ApiError(401, "invalid_publish_token", "登录已过期，请重新登录。");
  }
  return json({ valid: true, expiresAt: record.expires_at, user: publicUser(record) });
}

async function requireSessionUser(request, env) {
  const token = getCookie(request, SESSION_COOKIE);
  if (!token) throw new ApiError(401, "login_required", "请先登录。");
  const record = await env.COMMUNITY_DB.prepare(
    `SELECT users.id, users.email, users.display_name, users.publisher_handle, users.avatar_url,
            users.is_official, users.is_builder, users.email_verified_at, users.created_at
       FROM auth_sessions JOIN users ON users.id = auth_sessions.user_id
      WHERE auth_sessions.token_hash = ?1 AND auth_sessions.expires_at > ?2`,
  ).bind(await sha256Base64Url(token), new Date().toISOString()).first();
  if (!record) throw new ApiError(401, "login_required", "登录已过期，请重新登录。");
  return record;
}

async function enforceRateLimit(limiter, request) {
  if (!limiter) return;
  const result = await limiter.limit({ key: request.headers.get("cf-connecting-ip") || "local" });
  if (!result.success) {
    throw new ApiError(429, "rate_limited", "尝试次数过多，请稍后再试。");
  }
}

async function requireTurnstile(token, request, env) {
  if (await hasTurnstileClearance(request, env)) return;
  if (!env.TURNSTILE_SECRET_KEY || !env.TURNSTILE_SITE_KEY) throw new ApiError(503, "turnstile_unavailable", "人机验证服务暂时不可用，请稍后重试。");
  if (typeof token !== "string" || !token) throw new ApiError(403, "turnstile_required", "请先完成人机验证后再继续。");
  const form = new FormData();
  form.set("secret", env.TURNSTILE_SECRET_KEY);
  form.set("response", token);
  const remoteIp = request.headers.get("cf-connecting-ip");
  if (remoteIp) form.set("remoteip", remoteIp);
  const verification = await fetch("https://challenges.cloudflare.com/turnstile/v0/siteverify", { method: "POST", body: form });
  if (!verification.ok || !(await verification.json().catch(() => null))?.success) throw new ApiError(403, "turnstile_failed", "人机验证未通过，请重试。");
}

async function hasTurnstileClearance(request, env) {
  const value = getCookie(request, TURNSTILE_CLEARANCE_COOKIE);
  if (!value || !env.TURNSTILE_SECRET_KEY) return false;
  const [expiresAt, signature] = value.split(".");
  return /^\d+$/.test(expiresAt) && Number(expiresAt) > Date.now()
    && timingSafeEqual(base64UrlToBytes(signature || ""), base64UrlToBytes(await signTurnstileClearance(expiresAt, env)));
}

async function withTurnstileClearance(response, request, env) {
  if (await hasTurnstileClearance(request, env)) return response;
  const expiresAt = String(Date.now() + TURNSTILE_CLEARANCE_TTL_SECONDS * 1000);
  const headers = new Headers(response.headers);
  headers.append("set-cookie", `${TURNSTILE_CLEARANCE_COOKIE}=${expiresAt}.${await signTurnstileClearance(expiresAt, env)}; Path=/; Max-Age=${TURNSTILE_CLEARANCE_TTL_SECONDS}; HttpOnly; Secure; SameSite=Lax`);
  return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
}

async function signTurnstileClearance(expiresAt, env) {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(env.TURNSTILE_SECRET_KEY), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return bytesToBase64Url(new Uint8Array(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(expiresAt))));
}

async function mapCatalogResponse(env) {
  if (!env.MAP_BUCKET) {
    throw new ApiError(503, "service_unavailable", "地图服务尚未配置。");
  }

  const maps = [];
  let cursor;
  do {
    const page = await env.MAP_BUCKET.list({ limit: 1000, ...(cursor ? { cursor } : {}) });
    maps.push(...page.objects.filter((object) => object.key.toLowerCase().endsWith(".idvm")));
    cursor = page.truncated ? page.cursor : undefined;
  } while (cursor);

  maps.sort((left, right) => new Date(right.uploaded) - new Date(left.uploaded));
  const publications = await env.COMMUNITY_DB.prepare(
    "SELECT id, display_name, name, version, cover_key, content_key, publisher_key_id, created_at, updated_at FROM map_publications WHERE COALESCE(is_hidden, 0) = 0 ORDER BY updated_at DESC LIMIT 500",
  ).all();
  return json({ maps: maps.map((object) => ({
    name: safeFilename(object.key).replace(/\.idvm$/i, ""),
    filename: safeFilename(object.key),
    size: object.size,
    uploaded: object.uploaded,
    downloadUrl: `https://download.xgflee.com/maps/${encodeURIComponent(object.key)}`,
  })), publications: (publications.results || []).map((item) => ({
    id: item.id, name: item.name || item.display_name, publisherName: item.display_name, version: item.version,
    publishedAt: item.updated_at || item.created_at,
    coverUrl: item.cover_key ? `/api/maps/covers/${item.id}` : null,
    subscriptionLink: subscriptionLink("https://community.idvb.xgflee.com/", item.id, item.content_key, item.publisher_key_id),
  })) });
}

async function requirePublishToken(request, env) {
  const authorization = request.headers.get("authorization") || "";
  if (!authorization.startsWith("Bearer ")) throw new ApiError(401, "publish_token_required", "请先登录。");
  const record = await env.COMMUNITY_DB.prepare(
    `SELECT users.id, users.display_name, users.email, users.publisher_handle, users.avatar_url,
            users.is_official, users.is_builder, users.created_at, publish_tokens.expires_at
       FROM publish_tokens JOIN users ON users.id = publish_tokens.user_id
      WHERE publish_tokens.token_hash = ?1 AND publish_tokens.expires_at > ?2`,
  ).bind(await sha256Base64Url(authorization.slice(7)), new Date().toISOString()).first();
  if (!record) throw new ApiError(401, "invalid_publish_token", "登录已过期，请重新登录。");
  return record;
}

function base64ToBytes(value) {
  const binary = atob(value);
  return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}

function bytesToHex(bytes) {
  return [...bytes].map((value) => value.toString(16).padStart(2, "0")).join("").toUpperCase();
}

async function importPublisherKey(pem) {
  const base64 = pem.replace(/-----[^-]+-----/g, "").replace(/\s/g, "");
  return crypto.subtle.importKey("spki", base64ToBytes(base64), { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
}

async function publisherKeyId(pem) {
  const base64 = pem.replace(/-----[^-]+-----/g, "").replace(/\s/g, "");
  return bytesToHex(new Uint8Array(await crypto.subtle.digest("SHA-256", base64ToBytes(base64))));
}

async function registerResponse(request, env) {
  const body = await readJson(request);
  await requireTurnstile(body.turnstileToken, request, env);
  const email = normalizeEmail(body.email);
  const displayName = normalizeDisplayName(body.displayName);
  const password = validatePassword(body.password);

  const existing = await env.COMMUNITY_DB.prepare(
    "SELECT id FROM users WHERE email = ?1 COLLATE NOCASE",
  ).bind(email).first();
  if (existing) {
    throw new ApiError(409, "email_exists", "这个邮箱已经注册，可以直接登录。", { email: "邮箱已被使用" });
  }

  const now = new Date().toISOString();
  const user = { id: crypto.randomUUID(), email, displayName, isOfficial: false, isBuilder: false, createdAt: now };
  const passwordRecord = await hashPassword(password);

  try {
    await env.COMMUNITY_DB.prepare(
      `INSERT INTO users
        (id, email, display_name, password_hash, password_salt, password_iterations, created_at, updated_at)
       VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?7)`,
    ).bind(
      user.id,
      user.email,
      user.displayName,
      passwordRecord.hash,
      passwordRecord.salt,
      passwordRecord.iterations,
      now,
    ).run();
  } catch (error) {
    if (String(error?.message || error).toLowerCase().includes("unique")) {
      throw new ApiError(409, "email_exists", "这个邮箱已经注册，可以直接登录。", { email: "邮箱已被使用" });
    }
    throw error;
  }

  const session = await createSession(env.COMMUNITY_DB, user.id, request);
  return json({ user }, 201, { "set-cookie": session.cookie });
}

async function loginResponse(request, env) {
  const body = await readJson(request);
  await requireTurnstile(body.turnstileToken, request, env);
  const email = normalizeEmail(body.email);
  const password = typeof body.password === "string" ? body.password : "";
  const record = await env.COMMUNITY_DB.prepare(
    `SELECT id, email, display_name, password_hash, password_salt,
            password_iterations, avatar_url, is_official, is_builder, created_at
       FROM users WHERE email = ?1 COLLATE NOCASE`,
  ).bind(email).first();

  const valid = record && await verifyPassword(password, record);
  if (!valid) {
    throw new ApiError(401, "invalid_credentials", "邮箱或密码不正确。", { password: "请检查邮箱和密码" });
  }

  const session = await createSession(env.COMMUNITY_DB, record.id, request);
  return json({ user: publicUser(record) }, 200, { "set-cookie": session.cookie });
}

async function requestPasswordResetResponse(request, env) {
  const body = await readJson(request);
  await requireTurnstile(body.turnstileToken, request, env);
  const email = normalizeEmail(body.email);
  const user = await env.COMMUNITY_DB.prepare(
    "SELECT id, email FROM users WHERE email = ?1 COLLATE NOCASE",
  ).bind(email).first();
  const accepted = json({ message: "如果该邮箱已注册，验证码将很快发送。" });
  if (!user) return accepted;
  if (!env.PASSWORD_RESET_EMAIL || !env.PASSWORD_RESET_EMAIL_FROM)
    throw new ApiError(503, "email_unavailable", "验证码服务暂时不可用，请稍后重试。");

  const id = crypto.randomUUID();
  const code = String(crypto.getRandomValues(new Uint32Array(1))[0] % 1_000_000).padStart(6, "0");
  const now = new Date();
  await env.COMMUNITY_DB.prepare("DELETE FROM password_reset_codes WHERE user_id = ?1").bind(user.id).run();
  await env.COMMUNITY_DB.prepare(
    `INSERT INTO password_reset_codes (id, user_id, code_hash, created_at, expires_at)
     VALUES (?1, ?2, ?3, ?4, ?5)`,
  ).bind(id, user.id, await sha256Base64Url(`${id}:${code}`), now.toISOString(),
    new Date(now.getTime() + PASSWORD_RESET_TTL_SECONDS * 1000).toISOString()).run();
  try {
    await env.PASSWORD_RESET_EMAIL.send({
      from: { email: env.PASSWORD_RESET_EMAIL_FROM, name: "IDVB 地图社区" },
      to: user.email,
      subject: "IDVB 地图社区密码验证码",
      text: `你的密码验证码是：${code}\n\n验证码将在 10 分钟后失效。如非本人操作，请忽略此邮件。`,
      html: `<p>你的密码验证码是：</p><p style="font-size:28px;font-weight:700;letter-spacing:6px">${code}</p><p>验证码将在 10 分钟后失效。如非本人操作，请忽略此邮件。</p>`,
    });
  } catch (error) {
    await env.COMMUNITY_DB.prepare("DELETE FROM password_reset_codes WHERE id = ?1").bind(id).run();
    throw error;
  }
  return accepted;
}

async function confirmPasswordResetResponse(request, env) {
  const body = await readJson(request);
  await requireTurnstile(body.turnstileToken, request, env);
  const email = normalizeEmail(body.email);
  const code = typeof body.code === "string" ? body.code.trim() : "";
  const password = validatePassword(body.password);
  if (!/^\d{6}$/.test(code))
    throw new ApiError(400, "invalid_code", "请输入 6 位验证码。", { code: "请输入邮件中的 6 位验证码" });
  const record = await env.COMMUNITY_DB.prepare(
    `SELECT password_reset_codes.id, password_reset_codes.code_hash, password_reset_codes.attempts,
            password_reset_codes.expires_at, users.id AS user_id
       FROM password_reset_codes JOIN users ON users.id = password_reset_codes.user_id
      WHERE users.email = ?1 COLLATE NOCASE ORDER BY password_reset_codes.created_at DESC LIMIT 1`,
  ).bind(email).first();
  const valid = record && record.expires_at > new Date().toISOString() && record.attempts < 5
    && timingSafeEqual(base64UrlToBytes(record.code_hash), base64UrlToBytes(await sha256Base64Url(`${record.id}:${code}`)));
  if (!valid) {
    if (record) await env.COMMUNITY_DB.prepare(
      "UPDATE password_reset_codes SET attempts = attempts + 1 WHERE id = ?1",
    ).bind(record.id).run();
    throw new ApiError(400, "invalid_code", "验证码无效或已过期。", { code: "请重新获取验证码" });
  }

  const passwordRecord = await hashPassword(password);
  await env.COMMUNITY_DB.batch([
    env.COMMUNITY_DB.prepare(
      `UPDATE users SET password_hash=?2, password_salt=?3, password_iterations=?4, updated_at=?5 WHERE id=?1`,
    ).bind(record.user_id, passwordRecord.hash, passwordRecord.salt, passwordRecord.iterations, new Date().toISOString()),
    env.COMMUNITY_DB.prepare("DELETE FROM password_reset_codes WHERE user_id = ?1").bind(record.user_id),
    env.COMMUNITY_DB.prepare("DELETE FROM auth_sessions WHERE user_id = ?1").bind(record.user_id),
    env.COMMUNITY_DB.prepare("DELETE FROM publish_tokens WHERE user_id = ?1").bind(record.user_id),
  ]);
  return json({ reset: true, message: "密码已更改，请使用新密码登录。" });
}

async function currentUserResponse(request, env) {
  const token = getCookie(request, SESSION_COOKIE);
  if (!token) return json({ user: null });

  const tokenHash = await sha256Base64Url(token);
  const now = new Date().toISOString();
  const record = await env.COMMUNITY_DB.prepare(
    `SELECT users.id, users.email, users.display_name, users.avatar_url, users.is_official, users.is_builder, users.created_at
       FROM auth_sessions
       JOIN users ON users.id = auth_sessions.user_id
      WHERE auth_sessions.token_hash = ?1 AND auth_sessions.expires_at > ?2`,
  ).bind(tokenHash, now).first();

  if (!record) {
    return json({ user: null }, 200, { "set-cookie": clearSessionCookie() });
  }

  env.COMMUNITY_DB.prepare(
    "UPDATE auth_sessions SET last_seen_at = ?2 WHERE token_hash = ?1",
  ).bind(tokenHash, now).run().catch(() => {});
  return json({ user: publicUser(record) });
}

async function updateAvatarResponse(request, env, url) {
  const user = await requireSessionUser(request, env);
  const avatar = (await request.formData()).get("avatar");
  const allowedTypes = new Set(["image/png", "image/jpeg", "image/webp"]);
  if (!(avatar instanceof File) || !avatar.size || avatar.size > 2 * 1024 * 1024 || !allowedTypes.has(avatar.type))
    throw new ApiError(400, "invalid_avatar", "请选择不超过 2 MB 的 PNG、JPEG 或 WebP 图片。");
  await env.MAP_BUCKET.put(`community/avatars/${user.id}`, await avatar.arrayBuffer(), { httpMetadata: { contentType: avatar.type } });
  const avatarUrl = `${url.origin}/api/avatars/${user.id}?v=${Date.now()}`;
  await env.COMMUNITY_DB.prepare("UPDATE users SET avatar_url=?2, updated_at=?3 WHERE id=?1")
    .bind(user.id, avatarUrl, new Date().toISOString()).run();
  return json({ user: publicUser({ ...user, avatar_url: avatarUrl }) });
}

async function logoutResponse(request, env) {
  const token = getCookie(request, SESSION_COOKIE);
  if (token) {
    const tokenHash = await sha256Base64Url(token);
    await env.COMMUNITY_DB.prepare("DELETE FROM auth_sessions WHERE token_hash = ?1")
      .bind(tokenHash).run();
  }
  return json({ loggedOut: true }, 200, { "set-cookie": clearSessionCookie() });
}

async function createSession(database, userId, request) {
  const token = randomBase64Url(32);
  const tokenHash = await sha256Base64Url(token);
  const createdAt = new Date();
  const expiresAt = new Date(createdAt.getTime() + SESSION_TTL_SECONDS * 1000);
  await database.prepare(
    `INSERT INTO auth_sessions
      (token_hash, user_id, created_at, expires_at, last_seen_at, user_agent)
     VALUES (?1, ?2, ?3, ?4, ?3, ?5)`,
  ).bind(
    tokenHash,
    userId,
    createdAt.toISOString(),
    expiresAt.toISOString(),
    (request.headers.get("user-agent") || "").slice(0, 240),
  ).run();
  return {
    cookie: `${SESSION_COOKIE}=${token}; Path=/; Max-Age=${SESSION_TTL_SECONDS}; HttpOnly; Secure; SameSite=Lax`,
  };
}

async function hashPassword(password) {
  const saltBytes = crypto.getRandomValues(new Uint8Array(16));
  const hashBytes = await derivePassword(password, saltBytes, PASSWORD_ITERATIONS);
  return {
    hash: bytesToBase64Url(hashBytes),
    salt: bytesToBase64Url(saltBytes),
    iterations: PASSWORD_ITERATIONS,
  };
}

async function verifyPassword(password, record) {
  if (!password || !record.password_salt || !record.password_hash) return false;
  const actual = await derivePassword(
    password,
    base64UrlToBytes(record.password_salt),
    Number(record.password_iterations),
  );
  return timingSafeEqual(actual, base64UrlToBytes(record.password_hash));
}

async function derivePassword(password, salt, iterations) {
  const key = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(password),
    "PBKDF2",
    false,
    ["deriveBits"],
  );
  const bits = await crypto.subtle.deriveBits(
    { name: "PBKDF2", hash: "SHA-256", salt, iterations },
    key,
    256,
  );
  return new Uint8Array(bits);
}

function normalizeEmail(value) {
  const email = typeof value === "string" ? value.trim().toLowerCase() : "";
  if (email.length > 254 || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) {
    throw new ApiError(400, "invalid_input", "请填写有效的邮箱地址。", { email: "邮箱格式不正确" });
  }
  return email;
}

function normalizeDisplayName(value) {
  const displayName = typeof value === "string" ? value.trim().replace(/\s+/g, " ") : "";
  if (displayName.length < 2 || displayName.length > 32) {
    throw new ApiError(400, "invalid_input", "昵称需要 2–32 个字符。", { displayName: "请输入 2–32 个字符" });
  }
  return displayName;
}

function validatePassword(value) {
  const password = typeof value === "string" ? value : "";
  if (password.length < 10 || password.length > 128) {
    throw new ApiError(400, "invalid_input", "密码需要 10–128 个字符。", { password: "请输入至少 10 个字符" });
  }
  return password;
}

async function readJson(request) {
  if (!request.headers.get("content-type")?.toLowerCase().startsWith("application/json")) {
    throw new ApiError(415, "unsupported_media_type", "请求必须使用 JSON。" );
  }
  try {
    return await request.json();
  } catch {
    throw new ApiError(400, "invalid_json", "请求内容不是有效的 JSON。" );
  }
}

function rejectCrossSite(request, url) {
  const fetchSite = request.headers.get("sec-fetch-site");
  if (fetchSite === "cross-site") {
    throw new ApiError(403, "cross_site_request", "不接受跨站请求。" );
  }
  const origin = request.headers.get("origin");
  if (origin && origin !== url.origin) {
    throw new ApiError(403, "origin_mismatch", "请求来源不受信任。" );
  }
}

function publicUser(record) {
  return {
    id: record.id,
    email: record.email,
    displayName: record.display_name ?? record.displayName,
    publisherHandle: record.publisher_handle || `@u_${String(record.id).replace(/-/g, "").slice(0, 16)}`,
    avatarUrl: record.avatar_url ?? record.avatarUrl ?? null,
    isOfficial: Boolean(record.is_official ?? record.isOfficial),
    isBuilder: Boolean(record.is_builder ?? record.isBuilder),
    createdAt: record.created_at ?? record.createdAt,
  };
}

function getCookie(request, name) {
  const cookies = request.headers.get("cookie") || "";
  for (const part of cookies.split(";")) {
    const [key, ...value] = part.trim().split("=");
    if (key === name) return value.join("=");
  }
  return null;
}

function clearSessionCookie() {
  return `${SESSION_COOKIE}=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Lax`;
}

function safeFilename(key) {
  return (key.split("/").pop() || "map.idvm").replace(/[\\"\r\n]/g, "_");
}

function randomBase64Url(length) {
  return bytesToBase64Url(crypto.getRandomValues(new Uint8Array(length)));
}

async function sha256Base64Url(value) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return bytesToBase64Url(new Uint8Array(digest));
}

function bytesToBase64Url(bytes) {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function base64UrlToBytes(value) {
  const base64 = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
  const binary = atob(base64);
  return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}

function timingSafeEqual(left, right) {
  if (left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index += 1) difference |= left[index] ^ right[index];
  return difference === 0;
}

function json(body, status = 200, extraHeaders = {}) {
  return Response.json(body, { status, headers: { ...JSON_HEADERS, ...extraHeaders } });
}

class ApiError extends Error {
  constructor(status, code, message, fields) {
    super(message);
    this.status = status;
    this.code = code;
    this.fields = fields;
  }
}

async function requireAuthUser(request, env) {
  const authorization = request.headers.get("authorization") || "";
  if (authorization.startsWith("Bearer ")) {
    return await requirePublishToken(request, env);
  }
  return await requireSessionUser(request, env);
}

async function requireBuilder(request, env) {
  const user = await requireAuthUser(request, env);
  if (!user.is_builder && !user.is_official) {
    throw new ApiError(403, "builder_required", "只有获得建设者认证的用户才能访问该后台。");
  }
  return user;
}

async function requireSignedInBuilder(request, env) {
  rejectCrossSite(request, new URL(request.url));
  const user = await requireSessionUser(request, env);
  if (user.is_builder !== 1) {
    throw new ApiError(403, "builder_required", "只有已登录的建设者账户才能拉取反馈。");
  }
  return user;
}

function calculateWeightedLength(text) {
  if (!text || typeof text !== "string") return 0;
  const trimmed = text.trim();
  let len = 0;
  for (let i = 0; i < trimmed.length; i++) {
    const code = trimmed.charCodeAt(i);
    const isChinese = (code >= 0x4E00 && code <= 0x9FFF)
      || (code >= 0x3400 && code <= 0x4DBF)
      || (code >= 0xF900 && code <= 0xFAFF)
      || (code >= 0x3000 && code <= 0x303F)
      || (code >= 0xFF01 && code <= 0xFF5E)
      || code === 0x2014 || code === 0x2026
      || code === 0x2018 || code === 0x2019 || code === 0x201C || code === 0x201D;
    len += isChinese ? 2 : 1;
  }
  return len;
}

// Shared by both public anonymous intake routes.
// Limit the actual streamed body before multipart parsing, including chunked uploads.
async function boundedFeedbackRequest(request, maximum = 20 * 1024 * 1024) {
  if (Number(request.headers.get("content-length")) > maximum) {
    throw new ApiError(413, "feedback_too_large", "反馈附件总大小超出限制。");
  }
  const reader = request.body?.getReader();
  if (!reader) throw new ApiError(400, "invalid_feedback", "反馈内容为空。");
  const chunks = [];
  let size = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > maximum) {
        await reader.cancel();
        throw new ApiError(413, "feedback_too_large", "反馈附件总大小超出限制。");
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }
  return new Request(request.url, { method: "POST", headers: request.headers, body: new Blob(chunks) });
}

async function submitFeedbackResponse(request, env, anonymousAndroid = false) {
  const hasCredentials = request.headers.has("authorization") || Boolean(getCookie(request, SESSION_COOKIE));
  const user = anonymousAndroid || !hasCredentials ? { id: null } : await requireAuthUser(request, env);
  // Resolve credentials before parsing; an invalid token must not select the authenticated limits.
  if (!user.id) request = await boundedFeedbackRequest(request);
  const form = await publicationFormData(request);
  const contactQq = anonymousAndroid ? "" : String(form.get("contactQq") || "").trim();
  if (!anonymousAndroid && (!user.id || contactQq) && !/^[1-9][0-9]{4,11}$/.test(contactQq)) {
    throw new ApiError(400, "invalid_contact_qq", "未登录时请提供联系 QQ 号（5–12 位数字，不能以 0 开头）。");
  }
  const description = String(form.get("description") || "").trim();
  const suppliedVersion = String(form.get("clientVersion") || "unknown");
  const clientVersion = (anonymousAndroid
    ? `Android ${suppliedVersion.replace(/^Android\s*/i, "")}` : suppliedVersion).slice(0, 64);
  const logs = form.get("logs");
  const diagnostics = form.get("diagnostics");

  if (calculateWeightedLength(description) <= 10) {
    throw new ApiError(400, "invalid_description", "问题描述过短，必须大于 10 个字符（中文汉字算 2 字符）。");
  }
  if (description.length > 4000) {
    throw new ApiError(400, "invalid_description", "问题描述过长，不能超过 4000 字符。");
  }
  // Validate both attachments before writing either one.
  for (const [file, maximum, name] of [[logs, 30, "日志"], [diagnostics, 100, "诊断数据"]]) {
    if (file !== null && !(file instanceof File)) {
      throw new ApiError(400, "invalid_attachment", `${name}必须为 ZIP 附件。`);
    }
    if (file instanceof File && file.size > maximum * 1024 * 1024) {
      throw new ApiError(400, "attachment_too_large", `${name}压缩包不能超过 ${maximum} MB。`);
    }
  }

  const feedbackId = crypto.randomUUID();
  const day = new Date().toISOString().slice(0, 10);
  const now = new Date().toISOString();

  let hasLogs = 0;
  let logsKey = null;
  let logsSize = 0;

  if (logs instanceof File && logs.size > 0) {
    if (logs.size > 30 * 1024 * 1024) {
      throw new ApiError(400, "logs_too_large", "日志压缩包不能超过 30 MB。");
    }
    hasLogs = 1;
    logsKey = `feedbacks/${day}/${feedbackId}/logs.zip`;
    logsSize = logs.size;
    await env.MAP_BUCKET.put(logsKey, await logs.arrayBuffer(), {
      httpMetadata: { contentType: "application/zip", cacheControl: "no-store" },
      customMetadata: { feedbackId, userId: user.id || (anonymousAndroid ? "anonymous-android" : "anonymous-desktop"), clientVersion, type: "logs" },
    });
  }

  let hasDiagnostics = 0;
  let diagnosticsKey = null;
  let diagnosticsSize = 0;

  if (diagnostics instanceof File && diagnostics.size > 0) {
    if (diagnostics.size > 100 * 1024 * 1024) {
      throw new ApiError(400, "diagnostics_too_large", "诊断数据压缩包不能超过 100 MB。");
    }
    hasDiagnostics = 1;
    diagnosticsKey = `feedbacks/${day}/${feedbackId}/diagnostics.zip`;
    diagnosticsSize = diagnostics.size;
    await env.MAP_BUCKET.put(diagnosticsKey, await diagnostics.arrayBuffer(), {
      httpMetadata: { contentType: "application/zip", cacheControl: "no-store" },
      customMetadata: { feedbackId, userId: user.id || (anonymousAndroid ? "anonymous-android" : "anonymous-desktop"), clientVersion, type: "diagnostics" },
    });
  }

  const clientIp = request.headers.get("cf-connecting-ip") || "unknown";

  await env.COMMUNITY_DB.prepare(
    `INSERT INTO feedbacks
       (id, user_id, description, client_version, client_ip, has_logs, logs_key, logs_size,
        has_diagnostics, diagnostics_key, diagnostics_size, status, created_at, contact_qq)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, 'open', ?12, ?13)`
  ).bind(
    feedbackId, user.id, description, clientVersion, clientIp,
    hasLogs, logsKey, logsSize,
    hasDiagnostics, diagnosticsKey, diagnosticsSize, now, contactQq || null
  ).run();

  return json({
    success: true,
    message: "反馈提交成功，感谢您的支持！",
    feedbackId,
  }, 201);
}

async function builderUsersResponse(request, env) {
  await requireBuilder(request, env);
  const result = await env.COMMUNITY_DB.prepare(
    `SELECT id, email, display_name, avatar_url, is_official, is_builder, created_at
       FROM users ORDER BY created_at DESC LIMIT 500`
  ).all();
  return json({ users: (result.results || []).map(publicUser) });
}

async function certifyUserResponse(request, env, targetUserId) {
  const operator = await requireBuilder(request, env);
  const body = await readJson(request).catch(() => ({}));
  const isOfficial = body.isOfficial !== undefined ? (body.isOfficial ? 1 : 0) : 1;

  const target = await env.COMMUNITY_DB.prepare(
    "SELECT id, display_name, email, is_official, is_builder FROM users WHERE id = ?1"
  ).bind(targetUserId).first();

  if (!target) {
    throw new ApiError(404, "user_not_found", "目标用户不存在。");
  }

  // 建设者可以升级为已认证用户，但绝不能升级为建设者！
  const now = new Date().toISOString();
  await env.COMMUNITY_DB.prepare(
    "UPDATE users SET is_official = ?1, updated_at = ?2 WHERE id = ?3"
  ).bind(isOfficial, now, targetUserId).run();

  return json({
    success: true,
    message: isOfficial
      ? `已将用户 ${target.display_name} 设为已认证用户。`
      : `已取消用户 ${target.display_name} 的认证状态。`,
    user: {
      id: target.id,
      displayName: target.display_name,
      email: target.email,
      isOfficial: Boolean(isOfficial),
      isBuilder: Boolean(target.is_builder),
    },
  });
}

async function builderFeedbacksResponse(request, env) {
  await requireSignedInBuilder(request, env);
  const result = await env.COMMUNITY_DB.prepare(
    `SELECT f.id, f.user_id, u.display_name AS user_name, u.email AS user_email,
            f.description, f.client_version, f.client_ip, f.contact_qq,
            f.has_logs, f.logs_size,
            f.has_diagnostics, f.diagnostics_size,
            f.status, f.created_at
       FROM feedbacks f
       LEFT JOIN users u ON f.user_id = u.id
      ORDER BY f.created_at DESC LIMIT 200`
  ).all();
  return json({ feedbacks: result.results || [] });
}

async function builderFeedbackDownloadResponse(request, env, feedbackId, type) {
  await requireSignedInBuilder(request, env);
  if (type !== "logs" && type !== "diagnostics") {
    throw new ApiError(400, "invalid_type", "附件类型必须为 logs 或 diagnostics。");
  }

  const record = await env.COMMUNITY_DB.prepare(
    "SELECT logs_key, diagnostics_key FROM feedbacks WHERE id = ?1"
  ).bind(feedbackId).first();

  if (!record) {
    throw new ApiError(404, "feedback_not_found", "找不到指定的反馈记录。");
  }

  const key = type === "logs" ? record.logs_key : record.diagnostics_key;
  if (!key) {
    throw new ApiError(404, "attachment_not_found", "该反馈未包含对应的压缩包。");
  }

  const object = await env.MAP_BUCKET.get(key);
  if (!object) {
    throw new ApiError(404, "object_not_found", "存储桶中未找到对应文件。");
  }

  const headers = new Headers();
  object.writeHttpMetadata(headers);
  headers.set("content-type", "application/zip");
  headers.set("content-disposition", `attachment; filename="${type}-${feedbackId.slice(0, 8)}.zip"`);
  headers.set("access-control-expose-headers", "content-disposition");
  headers.set("cache-control", "no-store");
  headers.set("cross-origin-resource-policy", "same-origin");
  headers.set("x-content-type-options", "nosniff");

  return new Response(object.body, { headers });
}

async function builderMapsResponse(request, env) {
  await requireBuilder(request, env);
  const result = await env.COMMUNITY_DB.prepare(
    `SELECT m.id, m.display_name, m.name, m.version, m.cover_key, m.content_key,
            m.publisher_key_id, m.created_at, m.updated_at, m.is_hidden,
            m.user_id, u.display_name AS user_name, u.email AS user_email
       FROM map_publications m
       LEFT JOIN users u ON u.id = m.user_id
      ORDER BY m.updated_at DESC LIMIT 500`
  ).all();
  return json({
    maps: (result.results || []).map((item) => ({
      id: item.id,
      name: item.name || item.display_name,
      publisherName: item.display_name || item.user_name || "未知发布者",
      publisherEmail: item.user_email || null,
      version: item.version,
      coverUrl: item.cover_key ? `/api/maps/covers/${item.id}` : null,
      isHidden: Boolean(item.is_hidden),
      createdAt: item.created_at,
      updatedAt: item.updated_at || item.created_at,
    })),
  });
}

async function builderMapVisibilityResponse(request, env, publicationId) {
  await requireBuilder(request, env);
  const body = await request.json().catch(() => ({}));
  const hidden = body.hidden ? 1 : 0;

  const existing = await env.COMMUNITY_DB.prepare(
    "SELECT id FROM map_publications WHERE id = ?1"
  ).bind(publicationId).first();

  if (!existing) {
    throw new ApiError(404, "map_not_found", "找不到指定的地图。");
  }

  await env.COMMUNITY_DB.prepare(
    "UPDATE map_publications SET is_hidden = ?1 WHERE id = ?2"
  ).bind(hidden, publicationId).run();

  return json({ success: true, isHidden: Boolean(hidden) });
}

async function builderMapDeleteResponse(request, env, publicationId) {
  await requireBuilder(request, env);
  const existing = await env.COMMUNITY_DB.prepare(
    "SELECT id, feed_key, package_key, cover_key FROM map_publications WHERE id = ?1"
  ).bind(publicationId).first();

  if (!existing) {
    throw new ApiError(404, "map_not_found", "找不到指定的地图。");
  }

  if (env.MAP_BUCKET) {
    if (existing.package_key) {
      await env.MAP_BUCKET.delete(existing.package_key).catch(() => {});
    }
    if (existing.cover_key) {
      await env.MAP_BUCKET.delete(existing.cover_key).catch(() => {});
    }
    if (existing.feed_key) {
      await env.MAP_BUCKET.delete(existing.feed_key).catch(() => {});
    }
  }

  await env.COMMUNITY_DB.prepare(
    "DELETE FROM map_publications WHERE id = ?1"
  ).bind(publicationId).run();

  return json({ success: true });
}

function formatAnnouncement(item) {
  return {
    id: item.id,
    title: item.title,
    category: item.category,
    tag: item.tag || null,
    summary: item.summary || null,
    content: item.content,
    coverImageUrl: item.cover_image_url || null,
    authorId: item.author_id || null,
    authorName: item.author_name || null,
    isPinned: Boolean(item.is_pinned),
    isPublished: Boolean(item.is_published),
    priority: Number(item.priority || 0),
    minClientVersion: item.min_client_version || null,
    publishAt: item.publish_at,
    expiresAt: item.expires_at || null,
    createdAt: item.created_at,
    updatedAt: item.updated_at || item.created_at,
  };
}

async function announcementsListResponse(request, env, url) {
  const category = url.searchParams.get("category");
  const limitParam = parseInt(url.searchParams.get("limit") || "20", 10);
  const limit = Math.max(1, Math.min(isNaN(limitParam) ? 20 : limitParam, 50));
  const now = new Date().toISOString();

  let query = `
    SELECT a.id, a.title, a.category, a.tag, a.summary, a.content,
           a.cover_image_url, a.author_id, a.is_pinned, a.is_published,
           a.priority, a.min_client_version, a.publish_at, a.expires_at,
           a.created_at, a.updated_at,
           u.display_name AS author_name
      FROM announcements a
      LEFT JOIN users u ON u.id = a.author_id
     WHERE a.is_published = 1
       AND (a.expires_at IS NULL OR a.expires_at > ?1)
  `;
  const binds = [now];

  if (category && ["update", "tips", "notice"].includes(category)) {
    query += ` AND a.category = ?${binds.length + 1}`;
    binds.push(category);
  }

  query += ` ORDER BY a.is_pinned DESC, a.priority DESC, a.publish_at DESC LIMIT ?${binds.length + 1}`;
  binds.push(limit);

  const stmt = env.COMMUNITY_DB.prepare(query);
  const result = await stmt.bind(...binds).all();

  return json({
    announcements: (result.results || []).map(formatAnnouncement),
    timestamp: now,
  });
}

async function announcementDetailResponse(env, id) {
  const result = await env.COMMUNITY_DB.prepare(
    `SELECT a.id, a.title, a.category, a.tag, a.summary, a.content,
            a.cover_image_url, a.author_id, a.is_pinned, a.is_published,
            a.priority, a.min_client_version, a.publish_at, a.expires_at,
            a.created_at, a.updated_at,
            u.display_name AS author_name
       FROM announcements a
       LEFT JOIN users u ON u.id = a.author_id
      WHERE a.id = ?1`
  ).bind(id).first();

  if (!result || !result.is_published) {
    throw new ApiError(404, "announcement_not_found", "找不到指定的公告。");
  }

  return json({ announcement: formatAnnouncement(result) });
}

async function builderAnnouncementsResponse(request, env) {
  await requireBuilder(request, env);
  const result = await env.COMMUNITY_DB.prepare(
    `SELECT a.id, a.title, a.category, a.tag, a.summary, a.content,
            a.cover_image_url, a.author_id, a.is_pinned, a.is_published,
            a.priority, a.min_client_version, a.publish_at, a.expires_at,
            a.created_at, a.updated_at,
            u.display_name AS author_name
       FROM announcements a
       LEFT JOIN users u ON u.id = a.author_id
      ORDER BY a.is_pinned DESC, a.created_at DESC`
  ).all();
  return json({ announcements: (result.results || []).map(formatAnnouncement) });
}

async function createAnnouncementResponse(request, env) {
  const user = await requireBuilder(request, env);
  const body = await request.json().catch(() => ({}));
  const title = String(body.title || "").trim();
  const content = String(body.content || "").trim();
  const category = ["update", "tips", "notice"].includes(body.category) ? body.category : "notice";
  const tag = body.tag ? String(body.tag).trim().slice(0, 32) : null;
  const summary = body.summary ? String(body.summary).trim().slice(0, 500) : null;
  const coverImageUrl = body.coverImageUrl ? String(body.coverImageUrl).trim() : null;
  const isPinned = body.isPinned ? 1 : 0;
  const isPublished = body.isPublished !== undefined ? (body.isPublished ? 1 : 0) : 1;
  const priority = Number.isInteger(body.priority) ? body.priority : 0;
  const minClientVersion = body.minClientVersion ? String(body.minClientVersion).trim() : null;
  const now = new Date().toISOString();
  const publishAt = body.publishAt ? new Date(body.publishAt).toISOString() : now;
  const expiresAt = body.expiresAt ? new Date(body.expiresAt).toISOString() : null;

  if (!title) {
    throw new ApiError(400, "invalid_title", "公告标题不能为空。");
  }
  if (!content) {
    throw new ApiError(400, "invalid_content", "公告内容不能为空。");
  }

  const id = crypto.randomUUID();

  await env.COMMUNITY_DB.prepare(
    `INSERT INTO announcements (
      id, title, category, tag, summary, content,
      cover_image_url, author_id, is_pinned, is_published,
      priority, min_client_version, publish_at, expires_at,
      created_at, updated_at
    ) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16)`
  ).bind(
    id, title, category, tag, summary, content,
    coverImageUrl, user.id, isPinned, isPublished,
    priority, minClientVersion, publishAt, expiresAt,
    now, now
  ).run();

  return json({
    success: true,
    id,
    announcement: {
      id,
      title,
      category,
      tag,
      summary,
      content,
      coverImageUrl,
      authorId: user.id,
      authorName: user.display_name,
      isPinned: Boolean(isPinned),
      isPublished: Boolean(isPublished),
      priority,
      minClientVersion,
      publishAt,
      expiresAt,
      createdAt: now,
      updatedAt: now,
    },
  }, 201);
}

async function updateAnnouncementResponse(request, env, id) {
  const user = await requireBuilder(request, env);
  const body = await request.json().catch(() => ({}));
  const now = new Date().toISOString();

  const existing = await env.COMMUNITY_DB.prepare(
    "SELECT id FROM announcements WHERE id = ?1"
  ).bind(id).first();

  if (!existing) {
    throw new ApiError(404, "announcement_not_found", "找不到指定的公告。");
  }

  const updates = [];
  const binds = [];

  if (body.title !== undefined) {
    const title = String(body.title).trim();
    if (!title) throw new ApiError(400, "invalid_title", "公告标题不能为空。");
    updates.push(`title = ?${binds.length + 1}`);
    binds.push(title);
  }
  if (body.content !== undefined) {
    const content = String(body.content).trim();
    if (!content) throw new ApiError(400, "invalid_content", "公告内容不能为空。");
    updates.push(`content = ?${binds.length + 1}`);
    binds.push(content);
  }
  if (body.category !== undefined && ["update", "tips", "notice"].includes(body.category)) {
    updates.push(`category = ?${binds.length + 1}`);
    binds.push(body.category);
  }
  if (body.tag !== undefined) {
    updates.push(`tag = ?${binds.length + 1}`);
    binds.push(body.tag ? String(body.tag).trim().slice(0, 32) : null);
  }
  if (body.summary !== undefined) {
    updates.push(`summary = ?${binds.length + 1}`);
    binds.push(body.summary ? String(body.summary).trim().slice(0, 500) : null);
  }
  if (body.coverImageUrl !== undefined) {
    updates.push(`cover_image_url = ?${binds.length + 1}`);
    binds.push(body.coverImageUrl ? String(body.coverImageUrl).trim() : null);
  }
  if (body.isPinned !== undefined) {
    updates.push(`is_pinned = ?${binds.length + 1}`);
    binds.push(body.isPinned ? 1 : 0);
  }
  if (body.isPublished !== undefined) {
    updates.push(`is_published = ?${binds.length + 1}`);
    binds.push(body.isPublished ? 1 : 0);
  }
  if (body.priority !== undefined) {
    updates.push(`priority = ?${binds.length + 1}`);
    binds.push(Number.isInteger(body.priority) ? body.priority : 0);
  }
  if (body.minClientVersion !== undefined) {
    updates.push(`min_client_version = ?${binds.length + 1}`);
    binds.push(body.minClientVersion ? String(body.minClientVersion).trim() : null);
  }
  if (body.publishAt !== undefined) {
    updates.push(`publish_at = ?${binds.length + 1}`);
    binds.push(new Date(body.publishAt).toISOString());
  }
  if (body.expiresAt !== undefined) {
    updates.push(`expires_at = ?${binds.length + 1}`);
    binds.push(body.expiresAt ? new Date(body.expiresAt).toISOString() : null);
  }

  if (updates.length === 0) {
    return json({ success: true, message: "无更新字段。" });
  }

  updates.push(`updated_at = ?${binds.length + 1}`);
  binds.push(now);

  binds.push(id);
  const sql = `UPDATE announcements SET ${updates.join(", ")} WHERE id = ?${binds.length}`;
  await env.COMMUNITY_DB.prepare(sql).bind(...binds).run();

  return json({ success: true, updatedAt: now });
}

async function deleteAnnouncementResponse(request, env, id) {
  await requireBuilder(request, env);
  const existing = await env.COMMUNITY_DB.prepare(
    "SELECT id FROM announcements WHERE id = ?1"
  ).bind(id).first();

  if (!existing) {
    throw new ApiError(404, "announcement_not_found", "找不到指定的公告。");
  }

  await env.COMMUNITY_DB.prepare("DELETE FROM announcements WHERE id = ?1").bind(id).run();
  return json({ success: true });
}

export { publicationFormData as publicationFormDataForTest, boundedFeedbackRequest as boundedFeedbackRequestForTest };
