const UPLOAD_PATH = "/api/model-improvement/training-packages";
const ADMIN_ROOT = "/model-improvement-admin/";
const OBJECT_PREFIX = "training-packages/";
const MAX_PACKAGE_BYTES = 90 * 1024 * 1024;
const SESSION_SECONDS = 8 * 60 * 60;
const SESSION_COOKIE = "idvb_training_admin";
const HEX_SHA256 = /^[0-9a-f]{64}$/;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === UPLOAD_PATH) {
      return handleUpload(request, env);
    }
    if (url.pathname.startsWith(ADMIN_ROOT)) {
      return handleAdmin(request, env, url);
    }
    return secureResponse("Not Found", { status: 404 });
  },
};

async function handleUpload(request, env) {
  if (request.method !== "POST") {
    return secureResponse("Method Not Allowed", {
      status: 405,
      headers: { allow: "POST" },
    });
  }

  const clientKey = request.headers.get("cf-connecting-ip") || "unknown";
  const rateLimit = await env.UPLOAD_RATE_LIMITER.limit({ key: clientKey });
  if (!rateLimit.success) {
    return jsonResponse({ error: "rate_limited" }, 429, { "retry-after": "60" });
  }

  const contentType = request.headers.get("content-type")?.split(";", 1)[0].trim();
  const contentLength = Number(request.headers.get("content-length"));
  const datasetFingerprint = request.headers
    .get("x-idvb-dataset-fingerprint")?.toLowerCase();
  const packageSha256 = request.headers.get("x-idvb-package-sha256")?.toLowerCase();
  const sampleCount = Number(request.headers.get("x-idvb-sample-count"));
  const buildVersion = request.headers.get("x-idvb-build-version") || "";

  if (contentType !== "application/zip") {
    return jsonResponse({ error: "content_type_must_be_application_zip" }, 415);
  }
  if (!Number.isSafeInteger(contentLength)
      || contentLength <= 0
      || contentLength > MAX_PACKAGE_BYTES) {
    return jsonResponse({ error: "invalid_content_length" }, 413);
  }
  if (!HEX_SHA256.test(datasetFingerprint || "")
      || !HEX_SHA256.test(packageSha256 || "")
      || !Number.isSafeInteger(sampleCount)
      || sampleCount <= 0
      || sampleCount > 1_000_000
      || !/^[A-Za-z0-9._+-]{1,80}$/.test(buildVersion)) {
    return jsonResponse({ error: "invalid_training_package_metadata" }, 400);
  }
  if (!request.body) {
    return jsonResponse({ error: "missing_body" }, 400);
  }

  const now = new Date();
  const day = now.toISOString().slice(0, 10).replaceAll("-", "/");
  const objectKey = `${OBJECT_PREFIX}${day}/${datasetFingerprint}/${packageSha256}.zip`;
  const existing = await env.TRAINING_BUCKET.head(objectKey);
  if (!existing) {
    const checksum = hexToArrayBuffer(packageSha256);
    await env.TRAINING_BUCKET.put(objectKey, request.body, {
      sha256: checksum,
      httpMetadata: {
        contentType: "application/zip",
        contentDisposition: `attachment; filename="${safeFilename(buildVersion, now)}"`,
        cacheControl: "no-store",
      },
      customMetadata: {
        buildVersion,
        datasetFingerprint,
        packageSha256,
        sampleCount: String(sampleCount),
        receivedAt: now.toISOString(),
      },
    });
  }

  return jsonResponse({
    accepted: true,
    duplicate: Boolean(existing),
    datasetFingerprint,
    packageSha256,
  }, existing ? 200 : 201);
}

async function handleAdmin(request, env, url) {
  if (!env.ADMIN_TOKEN || env.ADMIN_TOKEN.length < 32) {
    return secureResponse("Administration is not configured.", { status: 503 });
  }

  if (url.pathname === `${ADMIN_ROOT}session` && request.method === "POST") {
    const form = await request.formData();
    const supplied = String(form.get("token") || "");
    if (!await timingSafeEqual(supplied, env.ADMIN_TOKEN)) {
      return loginPage("管理令牌无效。", 401);
    }
    const session = await createSession(env.ADMIN_TOKEN);
    return secureResponse(null, {
      status: 303,
      headers: {
        location: ADMIN_ROOT,
        "set-cookie": `${SESSION_COOKIE}=${session}; Path=${ADMIN_ROOT}; Max-Age=${SESSION_SECONDS}; HttpOnly; Secure; SameSite=Strict`,
      },
    });
  }

  if (url.pathname === `${ADMIN_ROOT}logout` && request.method === "POST") {
    return secureResponse(null, {
      status: 303,
      headers: {
        location: ADMIN_ROOT,
        "set-cookie": `${SESSION_COOKIE}=; Path=${ADMIN_ROOT}; Max-Age=0; HttpOnly; Secure; SameSite=Strict`,
      },
    });
  }

  const authorized = await hasValidSession(request, env.ADMIN_TOKEN);
  if (!authorized) {
    if (request.method !== "GET") {
      return secureResponse("Unauthorized", { status: 401 });
    }
    return loginPage();
  }

  if (url.pathname === ADMIN_ROOT && request.method === "GET") {
    return packageListPage(env.TRAINING_BUCKET);
  }
  if (url.pathname.startsWith(`${ADMIN_ROOT}packages/`)
      && (request.method === "GET" || request.method === "HEAD")) {
    return packageDownload(request, env.TRAINING_BUCKET, url.pathname);
  }
  return secureResponse("Not Found", { status: 404 });
}

async function packageListPage(bucket) {
  const packages = [];
  let cursor;
  do {
    const page = await bucket.list({
      prefix: OBJECT_PREFIX,
      limit: 1000,
      include: ["customMetadata"],
      ...(cursor ? { cursor } : {}),
    });
    packages.push(...page.objects);
    cursor = page.truncated ? page.cursor : undefined;
  } while (cursor && packages.length < 5000);

  packages.sort((left, right) => new Date(right.uploaded) - new Date(left.uploaded));
  const totalBytes = packages.reduce((total, item) => total + item.size, 0);
  const rows = packages.map((item) => {
    const metadata = item.customMetadata || {};
    const token = base64UrlEncode(new TextEncoder().encode(item.key));
    return `<tr>
      <td>${escapeHtml(new Date(item.uploaded).toLocaleString("zh-CN", { timeZone: "Asia/Tokyo" }))}</td>
      <td>${escapeHtml(metadata.buildVersion || "未知")}</td>
      <td>${escapeHtml(metadata.sampleCount || "未知")}</td>
      <td>${escapeHtml(formatBytes(item.size))}</td>
      <td><code>${escapeHtml((metadata.datasetFingerprint || "").slice(0, 12))}</code></td>
      <td><a class="button" href="${ADMIN_ROOT}packages/${token}">下载</a></td>
    </tr>`;
  }).join("");

  return htmlResponse(`<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>IDVB 训练包</title><style>${pageStyles()}</style></head>
<body><main><header><div><p class="eyebrow">IDENTITY VISION BRIDGE</p><h1>私有训练包</h1>
<p class="muted">${packages.length} 个训练包 · ${escapeHtml(formatBytes(totalBytes))}</p></div>
<form method="post" action="${ADMIN_ROOT}logout"><button type="submit" class="secondary">退出</button></form></header>
<section class="panel"><div class="table-wrap"><table><thead><tr><th>接收时间（东京）</th><th>客户端版本</th><th>样本</th><th>大小</th><th>数据集指纹</th><th></th></tr></thead>
<tbody>${rows || '<tr><td colspan="6" class="empty">尚未收到训练包</td></tr>'}</tbody></table></div></section>
<p class="footnote">此页面和所有下载均要求私有会话；R2 桶没有公开域名。</p></main></body></html>`);
}

async function packageDownload(request, bucket, path) {
  const token = path.slice(`${ADMIN_ROOT}packages/`.length);
  let key;
  try {
    key = new TextDecoder().decode(base64UrlDecode(token));
  } catch {
    return secureResponse("Invalid package identifier.", { status: 400 });
  }
  if (!key.startsWith(OBJECT_PREFIX)
      || !/^training-packages\/\d{4}\/\d{2}\/\d{2}\/[0-9a-f]{64}\/[0-9a-f]{64}\.zip$/.test(key)) {
    return secureResponse("Invalid package identifier.", { status: 400 });
  }

  const object = request.method === "HEAD"
    ? await bucket.head(key)
    : await bucket.get(key);
  if (!object) {
    return secureResponse("Package not found.", { status: 404 });
  }
  const headers = new Headers();
  object.writeHttpMetadata(headers);
  headers.set("content-type", "application/zip");
  headers.set("content-length", String(object.size));
  headers.set("content-disposition", object.httpMetadata?.contentDisposition
    || "attachment; filename=IDVB-training-package.zip");
  headers.set("cache-control", "no-store");
  headers.set("x-content-type-options", "nosniff");
  return new Response(request.method === "HEAD" ? null : object.body, { headers });
}

function loginPage(error = "", status = 200) {
  return htmlResponse(`<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>IDVB 训练包登录</title><style>${pageStyles()}</style></head>
<body><main class="login"><section class="panel"><p class="eyebrow">IDENTITY VISION BRIDGE</p><h1>训练包管理</h1>
<p class="muted">输入仅由你持有的管理令牌。令牌不会保存在浏览器存储中。</p>
${error ? `<p class="error">${escapeHtml(error)}</p>` : ""}
<form method="post" action="${ADMIN_ROOT}session"><label for="token">管理令牌</label>
<input id="token" name="token" type="password" required autocomplete="current-password" autofocus>
<button type="submit">进入私有空间</button></form></section></main></body></html>`, status);
}

async function createSession(secret) {
  const expires = Math.floor(Date.now() / 1000) + SESSION_SECONDS;
  const signature = await sign(String(expires), secret);
  return `${expires}.${signature}`;
}

async function hasValidSession(request, secret) {
  const cookie = request.headers.get("cookie") || "";
  const value = cookie.split(";").map((item) => item.trim())
    .find((item) => item.startsWith(`${SESSION_COOKIE}=`))
    ?.slice(SESSION_COOKIE.length + 1);
  const match = /^(\d{10})\.([A-Za-z0-9_-]{43})$/.exec(value || "");
  if (!match || Number(match[1]) < Math.floor(Date.now() / 1000)) return false;
  return timingSafeEqual(match[2], await sign(match[1], secret));
}

async function sign(value, secret) {
  const key = await crypto.subtle.importKey(
    "raw", new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return base64UrlEncode(new Uint8Array(
    await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(value))));
}

async function timingSafeEqual(left, right) {
  const leftHash = new Uint8Array(await crypto.subtle.digest(
    "SHA-256", new TextEncoder().encode(left)));
  const rightHash = new Uint8Array(await crypto.subtle.digest(
    "SHA-256", new TextEncoder().encode(right)));
  let difference = leftHash.length ^ rightHash.length;
  for (let index = 0; index < leftHash.length; index += 1) {
    difference |= leftHash[index] ^ rightHash[index];
  }
  return difference === 0;
}

function hexToArrayBuffer(value) {
  const bytes = new Uint8Array(value.length / 2);
  for (let index = 0; index < bytes.length; index += 1) {
    bytes[index] = Number.parseInt(value.slice(index * 2, index * 2 + 2), 16);
  }
  return bytes.buffer;
}

function base64UrlEncode(bytes) {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}

function base64UrlDecode(value) {
  if (!/^[A-Za-z0-9_-]+$/.test(value)) throw new Error("invalid base64url");
  const padded = value.replaceAll("-", "+").replaceAll("_", "/")
    + "=".repeat((4 - value.length % 4) % 4);
  const binary = atob(padded);
  return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}

function safeFilename(buildVersion, now) {
  return `IDVB-training-${now.toISOString().slice(0, 10)}-${buildVersion}.zip`;
}

function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KiB`;
  if (bytes < 1024 ** 3) return `${(bytes / 1024 ** 2).toFixed(1)} MiB`;
  return `${(bytes / 1024 ** 3).toFixed(2)} GiB`;
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (character) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  })[character]);
}

function pageStyles() {
  return `:root{color-scheme:dark;font-family:Inter,ui-sans-serif,system-ui,sans-serif;background:#0b0e13;color:#f5f7fb}*{box-sizing:border-box}body{margin:0;background:radial-gradient(circle at 20% 0,#19233a 0,transparent 42rem),#0b0e13;min-height:100vh}main{width:min(1100px,calc(100% - 32px));margin:0 auto;padding:64px 0}main.login{width:min(460px,calc(100% - 32px));padding-top:12vh}.panel{background:rgba(20,25,35,.88);border:1px solid #2b3447;border-radius:18px;padding:26px;box-shadow:0 24px 80px rgba(0,0,0,.3)}header{display:flex;align-items:flex-end;justify-content:space-between;gap:24px;margin-bottom:24px}h1{font-size:clamp(28px,5vw,44px);margin:5px 0 8px}.eyebrow{font-size:12px;letter-spacing:.2em;color:#8eb6ff;margin:0}.muted,.footnote{color:#aab4c7}.footnote{text-align:center;font-size:13px}.error{color:#ff9b9b}form{display:grid;gap:10px}label{font-size:14px;color:#cbd4e4}input{width:100%;border:1px solid #3a4760;background:#0d111a;color:#fff;border-radius:10px;padding:12px;font:inherit}button,.button{border:0;border-radius:10px;background:#74a7ff;color:#07101f;padding:10px 15px;font:600 14px inherit;text-decoration:none;cursor:pointer}.secondary{background:#222b3b;color:#dbe4f4}.table-wrap{overflow:auto}table{width:100%;border-collapse:collapse;white-space:nowrap}th,td{text-align:left;padding:13px 12px;border-bottom:1px solid #2a3344}th{color:#9eabc0;font-size:12px;text-transform:uppercase;letter-spacing:.08em}td{font-size:14px}.empty{text-align:center;color:#929db0;padding:50px}code{color:#a8c8ff}`;
}

function secureResponse(body, init = {}) {
  const headers = new Headers(init.headers);
  headers.set("cache-control", "no-store");
  headers.set("content-security-policy", "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'");
  headers.set("referrer-policy", "no-referrer");
  headers.set("x-content-type-options", "nosniff");
  headers.set("x-frame-options", "DENY");
  return new Response(body, { ...init, headers });
}

function htmlResponse(html, status = 200) {
  return secureResponse(html, {
    status,
    headers: { "content-type": "text/html; charset=utf-8" },
  });
}

function jsonResponse(value, status = 200, extraHeaders = {}) {
  return secureResponse(JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json; charset=utf-8", ...extraHeaders },
  });
}

export const testing = {
  ADMIN_ROOT,
  UPLOAD_PATH,
  createSession,
};
