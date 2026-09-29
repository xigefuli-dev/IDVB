import assert from "node:assert/strict";
import test from "node:test";
import worker, { publicationFormDataForTest } from "../src/index.js";

const nativeFetch = globalThis.fetch;
globalThis.fetch = async (input, init) => String(input) === "https://challenges.cloudflare.com/turnstile/v0/siteverify"
  ? Response.json({ success: true }) : nativeFetch(input, init);

function createEnvironment() {
  const users = new Map();
  const sessions = new Map();
  const publishTokens = new Map();
  const authorizationCodes = new Map();
  const passwordResetCodes = new Map();
  const sentEmails = [];
  const publications = new Map();
  const feedbacks = new Map();
  const announcements = new Map();
  const bucketObjects = new Map();
  const database = {
    prepare(sql) {
      return {
        values: [],
        bind(...values) { this.values = values; return this; },
        async first() {
          if (sql.includes("FROM users WHERE email")) {
            const user = [...users.values()].find((item) => item.email.toLowerCase() === this.values[0].toLowerCase());
            if (!user) return null;
            return sql.startsWith("SELECT id FROM") ? { id: user.id } : user;
          }
          if (sql.includes("FROM users WHERE id = ?1")) {
            return users.get(this.values[0]) || null;
          }
          if (sql.includes("FROM auth_sessions") && sql.includes("JOIN users")) {
            const session = sessions.get(this.values[0]);
            if (!session || session.expires_at <= this.values[1]) return null;
            return users.get(session.user_id) || null;
          }
          if (sql.includes("FROM publish_tokens") && sql.includes("JOIN users")) {
            const token = publishTokens.get(this.values[0]);
            if (!token || token.expires_at <= this.values[1]) return null;
            return { ...users.get(token.user_id), expires_at: token.expires_at };
          }
          if (sql.includes("FROM oauth_authorization_codes") && sql.includes("JOIN users")) {
            assert.match(sql, /users\.id AS id/);
            const authorization = authorizationCodes.get(this.values[0]);
            if (!authorization || authorization.expires_at <= this.values[1]) return null;
            return { ...authorization, ...users.get(authorization.user_id) };
          }
          if (sql.includes("FROM password_reset_codes") && sql.includes("JOIN users")) {
            const user = [...users.values()].find((item) => item.email.toLowerCase() === this.values[0].toLowerCase());
            return [...passwordResetCodes.values()].find((item) => item.user_id === user?.id) || null;
          }
          if (sql.includes("FROM map_publications WHERE id")) return publications.get(this.values[0]) || null;
          if (sql.includes("FROM feedbacks WHERE id")) return feedbacks.get(this.values[0]) || null;
          if (sql.includes("FROM announcements")) return announcements.get(this.values[0]) || null;
          throw new Error(`Unhandled first query: ${sql}`);
        },
        async all() {
          if (sql.includes("FROM map_publications")) {
            let list = [...publications.values()];
            if (sql.includes("COALESCE(is_hidden, 0) = 0")) {
              list = list.filter((item) => !item.is_hidden);
            }
            if (sql.includes("LEFT JOIN users")) {
              list = list.map((item) => {
                const user = users.get(item.user_id);
                return {
                  ...item,
                  user_name: user?.display_name,
                  user_email: user?.email,
                };
              });
            }
            return { results: list };
          }
          if (sql.includes("FROM users")) return { results: [...users.values()] };
          if (sql.includes("FROM feedbacks")) {
            return { results: [...feedbacks.values()].map((item) => {
              const result = { ...item };
              if (!sql.includes("f.logs_key")) delete result.logs_key;
              if (!sql.includes("f.diagnostics_key")) delete result.diagnostics_key;
              return result;
            }) };
          }
          if (sql.includes("FROM announcements")) {
            let list = [...announcements.values()];
            if (sql.includes("WHERE a.is_published = 1")) {
              list = list.filter((item) => item.is_published);
            }
            if (sql.includes("a.category =")) {
              const cat = this.values.find((v) => ["update", "tips", "notice"].includes(v));
              if (cat) list = list.filter((item) => item.category === cat);
            }
            list.sort((a, b) => (b.is_pinned || 0) - (a.is_pinned || 0) || (b.priority || 0) - (a.priority || 0) || new Date(b.publish_at) - new Date(a.publish_at));
            return { results: list };
          }
          throw new Error(`Unhandled all query: ${sql}`);
        },
        async run() {
          if (sql.includes("INSERT INTO users")) {
            const [id, email, display_name, password_hash, password_salt, password_iterations, created_at] = this.values;
            if ([...users.values()].some((item) => item.email.toLowerCase() === email.toLowerCase())) throw new Error("UNIQUE constraint failed");
            users.set(id, { id, email, display_name, password_hash, password_salt, password_iterations, created_at });
          } else if (sql.includes("INSERT INTO auth_sessions")) {
            const [token_hash, user_id, created_at, expires_at, user_agent] = this.values;
            sessions.set(token_hash, { token_hash, user_id, created_at, expires_at, last_seen_at: created_at, user_agent });
          } else if (sql.includes("INSERT INTO publish_tokens")) {
            const [token_hash, user_id, created_at, expires_at] = this.values;
            publishTokens.set(token_hash, { token_hash, user_id, created_at, expires_at });
          } else if (sql.includes("INSERT INTO oauth_authorization_codes")) {
            const [code_hash, user_id, client_id, redirect_uri, code_challenge, created_at, expires_at] = this.values;
            authorizationCodes.set(code_hash, { code_hash, user_id, client_id, redirect_uri, code_challenge, created_at, expires_at });
          } else if (sql.includes("INSERT INTO password_reset_codes")) {
            const [id, user_id, code_hash, created_at, expires_at] = this.values;
            passwordResetCodes.set(id, { id, user_id, code_hash, attempts: 0, created_at, expires_at });
          } else if (sql.includes("UPDATE auth_sessions")) {
            const session = sessions.get(this.values[0]);
            if (session) session.last_seen_at = this.values[1];
          } else if (sql.includes("DELETE FROM auth_sessions")) {
            if (sql.includes("user_id")) {
              for (const [id, item] of sessions) if (item.user_id === this.values[0]) sessions.delete(id);
            } else sessions.delete(this.values[0]);
          } else if (sql.includes("DELETE FROM publish_tokens")) {
            if (sql.includes("user_id")) {
              for (const [id, item] of publishTokens) if (item.user_id === this.values[0]) publishTokens.delete(id);
            } else publishTokens.delete(this.values[0]);
          } else if (sql.includes("DELETE FROM oauth_authorization_codes")) {
            authorizationCodes.delete(this.values[0]);
          } else if (sql.includes("DELETE FROM password_reset_codes WHERE user_id")) {
            for (const [id, item] of passwordResetCodes) if (item.user_id === this.values[0]) passwordResetCodes.delete(id);
          } else if (sql.includes("DELETE FROM password_reset_codes WHERE id")) {
            passwordResetCodes.delete(this.values[0]);
          } else if (sql.includes("UPDATE password_reset_codes")) {
            passwordResetCodes.get(this.values[0]).attempts += 1;
          } else if (sql.includes("UPDATE users SET password_hash")) {
            const [id, password_hash, password_salt, password_iterations] = this.values;
            Object.assign(users.get(id), { password_hash, password_salt, password_iterations });
          } else if (sql.includes("UPDATE users SET avatar_url")) {
            const [id, avatar_url] = this.values;
            Object.assign(users.get(id), { avatar_url });
          } else if (sql.includes("INSERT INTO map_publications")) {
            const [id, display_name, name, version, feed_key, package_key, cover_key, publisher_key_id,
              created_at, user_id, content_key] = this.values;
            publications.set(id, { id, user_id, display_name, name, version, feed_key, package_key,
              cover_key, content_key, publisher_key_id, created_at, updated_at: created_at });
          } else if (sql.includes("UPDATE map_publications SET is_hidden")) {
            const [is_hidden, id] = this.values;
            const target = publications.get(id);
            if (target) target.is_hidden = is_hidden;
          } else if (sql.includes("DELETE FROM map_publications WHERE id")) {
            publications.delete(this.values[0]);
          } else if (sql.includes("UPDATE map_publications")) {
            const [id, display_name, name, version, feed_key, package_key, cover_key, publisher_key_id,
              updated_at] = this.values;
            Object.assign(publications.get(id), { display_name, name, version, feed_key, package_key,
              cover_key, publisher_key_id, updated_at });
          } else if (sql.includes("UPDATE users SET is_official")) {
            const [is_official, updated_at, id] = this.values;
            const user = users.get(id);
            if (user) Object.assign(user, { is_official, updated_at });
          } else if (sql.includes("INSERT INTO feedbacks")) {
            const [id, user_id, description, client_version, client_ip, has_logs, logs_key, logs_size,
              has_diagnostics, diagnostics_key, diagnostics_size, created_at, contact_qq] = this.values;
            feedbacks.set(id, { id, user_id, description, client_version, client_ip, has_logs, logs_key, logs_size,
              has_diagnostics, diagnostics_key, diagnostics_size, status: 'open', created_at, contact_qq });
          } else if (sql.includes("INSERT INTO announcements")) {
            const [id, title, category, tag, summary, content, cover_image_url, author_id, is_pinned, is_published, priority, min_client_version, publish_at, expires_at, created_at, updated_at] = this.values;
            announcements.set(id, { id, title, category, tag, summary, content, cover_image_url, author_id, is_pinned, is_published: is_published ?? 1, priority, min_client_version, publish_at, expires_at, created_at, updated_at });
          } else if (sql.includes("UPDATE announcements SET")) {
            const id = this.values[this.values.length - 1];
            const target = announcements.get(id);
            if (target) {
              if (sql.includes("title =")) target.title = this.values[0];
            }
          } else if (sql.includes("DELETE FROM announcements WHERE id")) {
            announcements.delete(this.values[0]);
          } else {
            throw new Error(`Unhandled run query: ${sql}`);
          }
          return { success: true };
        },
      };
    },
    async batch(statements) { for (const statement of statements) await statement.run(); },
  };
  return {
    COMMUNITY_DB: database,
    feedbacks,
    users,
    publications,
    announcements,
    bucketObjects,
    ASSETS: { fetch: async () => new Response("community-home", { headers: { "content-type": "text/html" } }) },
    MAP_BUCKET: {
      list: async () => ({
        objects: [
          { key: "Hospital.idvm", size: 2048, uploaded: new Date("2026-08-30T00:00:00Z") },
          { key: "ignore.txt", size: 10, uploaded: new Date("2026-08-31T00:00:00Z") },
        ],
        truncated: false,
      }),
      put: async (key, value) => bucketObjects.set(key, new Uint8Array(await new Response(value).arrayBuffer())),
      get: async (key) => {
        const value = bucketObjects.get(key);
        return value ? {
          body: value,
          size: value.length,
          writeHttpMetadata() {}
        } : null;
      },
      delete: async (key) => { bucketObjects.delete(key); },
    },
    LOGIN_RATE_LIMITER: { limit: async () => ({ success: true }) },
    REGISTER_RATE_LIMITER: { limit: async () => ({ success: true }) },
    PASSWORD_RESET_RATE_LIMITER: { limit: async () => ({ success: true }) },
    PUBLISH_TOKEN_RATE_LIMITER: { limit: async () => ({ success: true }) },
    FEEDBACK_RATE_LIMITER: { limit: async () => ({ success: true }) },
    PASSWORD_RESET_EMAIL_FROM: "accounts@idvb.test",
    PASSWORD_RESET_EMAIL: { send: async (message) => sentEmails.push(message) },
    TURNSTILE_SITE_KEY: "1x00000000000000000000AA",
    TURNSTILE_SECRET_KEY: "1x0000000000000000000000000000000AA",
    users,
    sessions,
    publishTokens,
    authorizationCodes,
    passwordResetCodes,
    sentEmails,
    publications,
    bucketObjects,
  };
}

function jsonRequest(path, body, headers = {}) {
  return new Request(`https://community.idvb.test${path}`, {
    method: "POST",
    headers: { "content-type": "application/json", origin: "https://community.idvb.test", ...headers },
    body: JSON.stringify({ turnstileToken: "test-token", ...body }),
  });
}

test("static requests are delegated to the asset binding", async () => {
  const response = await worker.fetch(new Request("https://community.idvb.test/"), createEnvironment());
  assert.equal(response.status, 200);
  assert.equal(await response.text(), "community-home");
});

test("web app assets use their own local-media policy and scoped URL", async () => {
  const env = createEnvironment();
  const redirect = await worker.fetch(new Request("https://community.idvb.test/web"), env);
  assert.equal(redirect.status, 308);
  assert.equal(redirect.headers.get("location"), "https://community.idvb.test/web/");

  const response = await worker.fetch(new Request("https://community.idvb.test/web/"), env);
  assert.equal(response.status, 200);
  assert.equal(await response.text(), "community-home");
  const policy = response.headers.get("content-security-policy");
  assert.match(policy, /worker-src 'self'/);
  assert.match(policy, /img-src 'self' blob: data:/);
  assert.match(policy, /media-src 'self' blob:/);
  assert.equal(response.headers.get("cache-control"), "public, no-cache, no-transform");
});

test("map catalog exposes real IDVM objects and download links", async () => {
  const response = await worker.fetch(new Request("https://community.idvb.test/api/maps"), createEnvironment());
  assert.deepEqual(await response.json(), {
    maps: [{
      name: "Hospital",
      filename: "Hospital.idvm",
      size: 2048,
      uploaded: "2026-08-30T00:00:00.000Z",
      downloadUrl: "https://download.xgflee.com/maps/Hospital.idvm",
    }], publications: [],
  });
});

test("registration creates a hashed account and authenticated session", async () => {
  const env = createEnvironment();
  const response = await worker.fetch(jsonRequest("/api/auth/register", {
    displayName: "地图旅人",
    email: "Traveler@Example.com",
    password: "correct-horse-map-battery",
  }), env);

  assert.equal(response.status, 201);
  const body = await response.json();
  assert.equal(body.user.email, "traveler@example.com");
  assert.equal(body.user.displayName, "地图旅人");
  assert.equal(body.user.isOfficial, false);
  assert.equal(body.user.isBuilder, false);
  assert.equal(env.users.size, 1);
  const stored = [...env.users.values()][0];
  assert.notEqual(stored.password_hash, "correct-horse-map-battery");
  assert.equal(stored.password_iterations, 100_000);
  assert.equal(env.sessions.size, 1);
  assert.match(response.headers.get("set-cookie"), /HttpOnly; Secure; SameSite=Lax/);

  const cookie = response.headers.get("set-cookie").split(";", 1)[0];
  const me = await worker.fetch(new Request("https://community.idvb.test/api/auth/me", { headers: { cookie } }), env);
  assert.equal((await me.json()).user.displayName, "地图旅人");
});

test("duplicate registration is rejected without replacing the account", async () => {
  const env = createEnvironment();
  const account = { displayName: "地图旅人", email: "same@example.com", password: "correct-horse-map-battery" };
  assert.equal((await worker.fetch(jsonRequest("/api/auth/register", account), env)).status, 201);
  const response = await worker.fetch(jsonRequest("/api/auth/register", { ...account, displayName: "另一个人" }), env);
  assert.equal(response.status, 409);
  assert.equal((await response.json()).error, "email_exists");
  assert.equal(env.users.size, 1);
});

test("login verifies the password and logout revokes the session", async () => {
  const env = createEnvironment();
  const account = { displayName: "Map Maker", email: "maker@example.com", password: "a-safe-password-123" };
  await worker.fetch(jsonRequest("/api/auth/register", account), env);
  [...env.users.values()][0].is_official = 1;
  [...env.users.values()][0].is_builder = 1;

  const rejected = await worker.fetch(jsonRequest("/api/auth/login", { email: account.email, password: "wrong-password" }), env);
  assert.equal(rejected.status, 401);

  const login = await worker.fetch(jsonRequest("/api/auth/login", { email: account.email, password: account.password }), env);
  assert.equal(login.status, 200);
  assert.equal((await login.clone().json()).user.isOfficial, true);
  assert.equal((await login.clone().json()).user.isBuilder, true);
  const cookie = login.headers.get("set-cookie").split(";", 1)[0];
  assert.equal(env.sessions.size, 2);

  const logout = await worker.fetch(jsonRequest("/api/auth/logout", {}, { cookie }), env);
  assert.equal(logout.status, 200);
  assert.equal(env.sessions.size, 1);
  assert.match(logout.headers.get("set-cookie"), /Max-Age=0/);
});

test("authenticated user uploads an avatar shared by web and desktop identity", async () => {
  const env = createEnvironment();
  const account = { displayName: "Map Maker", email: "avatar@example.com", password: "a-safe-password-123" };
  const registration = await worker.fetch(jsonRequest("/api/auth/register", account), env);
  const cookie = registration.headers.get("set-cookie").split(";", 1)[0];
  const form = new FormData();
  form.set("avatar", new File([new Uint8Array([1, 2, 3])], "avatar.png", { type: "image/png" }));
  const response = await worker.fetch(new Request("https://community.idvb.test/api/auth/avatar", {
    method: "POST", headers: { cookie, origin: "https://community.idvb.test" }, body: form,
  }), env);
  assert.equal(response.status, 200);
  const user = (await response.json()).user;
  assert.match(user.avatarUrl, /^https:\/\/community\.idvb\.test\/api\/avatars\//);
  assert.equal(env.bucketObjects.size, 1);
  const avatar = await worker.fetch(new Request(user.avatarUrl), env);
  assert.equal(avatar.status, 200);
  assert.deepEqual([...new Uint8Array(await avatar.arrayBuffer())], [1, 2, 3]);
});

test("password reset emails a one-time code, changes the password, and revokes sessions", async () => {
  const env = createEnvironment();
  const account = { displayName: "Map Maker", email: "reset@example.com", password: "old-safe-password-123" };
  await worker.fetch(jsonRequest("/api/auth/register", account), env);

  const requested = await worker.fetch(jsonRequest("/api/auth/password-reset/request", { email: account.email }), env);
  assert.equal(requested.status, 200);
  assert.equal(env.sentEmails.length, 1);
  assert.equal(env.sentEmails[0].to, account.email);
  const code = /\d{6}/.exec(env.sentEmails[0].text)[0];

  const wrong = await worker.fetch(jsonRequest("/api/auth/password-reset/confirm", {
    email: account.email, code: code === "000000" ? "000001" : "000000", password: "new-safe-password-456",
  }), env);
  assert.equal(wrong.status, 400);

  const reset = await worker.fetch(jsonRequest("/api/auth/password-reset/confirm", {
    email: account.email, code, password: "new-safe-password-456",
  }), env);
  assert.equal(reset.status, 200);
  assert.equal(env.passwordResetCodes.size, 0);
  assert.equal(env.sessions.size, 0);
  assert.equal((await worker.fetch(jsonRequest("/api/auth/login", {
    email: account.email, password: account.password,
  }), env)).status, 401);
  assert.equal((await worker.fetch(jsonRequest("/api/auth/login", {
    email: account.email, password: "new-safe-password-456",
  }), env)).status, 200);
});

test("weak input and cross-site writes are rejected", async () => {
  const env = createEnvironment();
  const weak = await worker.fetch(jsonRequest("/api/auth/register", {
    displayName: "A",
    email: "not-an-email",
    password: "short",
  }), env);
  assert.equal(weak.status, 400);

  const crossSite = await worker.fetch(jsonRequest("/api/auth/login", {
    email: "maker@example.com",
    password: "a-safe-password-123",
  }, { origin: "https://evil.example", "sec-fetch-site": "cross-site" }), env);
  assert.equal(crossSite.status, 403);
});

test("login rate limiting fails closed before password verification", async () => {
  const env = createEnvironment();
  env.LOGIN_RATE_LIMITER = { limit: async () => ({ success: false }) };
  const response = await worker.fetch(jsonRequest("/api/auth/login", {
    email: "maker@example.com",
    password: "a-safe-password-123",
  }), env);
  assert.equal(response.status, 429);
  assert.equal((await response.json()).error, "rate_limited");
});

test("OAuth capability advertises the desktop authorization endpoint", async () => {
  const response = await worker.fetch(new Request("https://community.idvb.test/api/auth/capabilities"), createEnvironment());
  assert.deepEqual(await response.json(), {
    password: true,
    oauth: true,
    oauthAuthorizePath: "/oauth/authorize",
    turnstileSiteKey: "1x00000000000000000000AA",
    turnstileRequired: true,
  });
});

test("password operations require Turnstile once and reuse the signed ten-minute clearance", async () => {
  const env = createEnvironment();
  const missing = await worker.fetch(jsonRequest("/api/auth/register", {
    displayName: "地图旅人", email: "human@example.com", password: "correct-horse-map-battery", turnstileToken: "",
  }), env);
  assert.equal(missing.status, 403);
  const registration = await worker.fetch(jsonRequest("/api/auth/register", {
    displayName: "地图旅人", email: "human@example.com", password: "correct-horse-map-battery",
  }), env);
  const clearance = registration.headers.get("set-cookie").split(", ").find((cookie) => cookie.startsWith("idvb_turnstile_clearance="));
  assert.ok(clearance);
  const login = await worker.fetch(jsonRequest("/api/auth/login", {
    email: "human@example.com", password: "correct-horse-map-battery", turnstileToken: "",
  }, { cookie: clearance.split(";", 1)[0] }), env);
  assert.equal(login.status, 200);
});

test("publish tokens require an online session and remain server-verifiable", async () => {
  const env = createEnvironment();
  const account = { displayName: "Map Maker", email: "publish@example.com", password: "a-safe-password-123" };
  const registration = await worker.fetch(jsonRequest("/api/auth/register", account), env);
  const cookie = registration.headers.get("set-cookie").split(";", 1)[0];

  const issued = await worker.fetch(jsonRequest("/api/auth/publish-token", {}, { cookie }), env);
  assert.equal(issued.status, 200);
  const body = await issued.json();
  assert.match(body.user.publisherHandle, /^@u_[a-f0-9]{16}$/);
  assert.equal(env.publishTokens.size, 1);

  const verified = await worker.fetch(new Request("https://community.idvb.test/api/auth/publish-token", {
    headers: { authorization: `Bearer ${body.token}` },
  }), env);
  assert.equal(verified.status, 200);
  assert.equal((await verified.json()).valid, true);

  const revoked = await worker.fetch(new Request("https://community.idvb.test/api/auth/publish-token", {
    method: "DELETE", headers: { authorization: `Bearer ${body.token}` },
  }), env);
  assert.equal(revoked.status, 200);
  assert.equal(env.publishTokens.size, 0);

  const forged = await worker.fetch(new Request("https://community.idvb.test/api/auth/publish-token", {
    headers: { authorization: "Bearer locally-forged" },
  }), env);
  assert.equal(forged.status, 401);
});

test("desktop OAuth uses PKCE and consumes each authorization code once", async () => {
  const env = createEnvironment();
  const registration = await worker.fetch(jsonRequest("/api/auth/register", {
    displayName: "OAuth Maker", email: "oauth@example.com", password: "a-safe-password-123",
  }), env);
  const cookie = registration.headers.get("set-cookie").split(";", 1)[0];
  const verifier = "v".repeat(43);
  const challenge = Buffer.from(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier)))
    .toString("base64url");
  const oauth = {
    clientId: "idvb-desktop", redirectUri: "http://127.0.0.1:43210/callback",
    codeChallenge: challenge, state: "s".repeat(32),
  };
  const authorization = await worker.fetch(jsonRequest("/api/oauth/authorize", oauth, { cookie }), env);
  assert.equal(authorization.status, 200);
  const callback = new URL((await authorization.json()).redirectUrl);
  assert.equal(callback.searchParams.get("state"), oauth.state);
  const exchange = { ...oauth, code: callback.searchParams.get("code"), codeVerifier: verifier };
  const token = await worker.fetch(jsonRequest("/api/oauth/token", exchange), env);
  assert.equal(token.status, 200);
  assert.equal(typeof (await token.json()).token, "string");
  assert.equal(env.authorizationCodes.size, 0);
  assert.equal((await worker.fetch(jsonRequest("/api/oauth/token", exchange), env)).status, 401);
});

test("authenticated map publication verifies signature and stores package before feed", async () => {
  const env = createEnvironment();
  const registration = await worker.fetch(jsonRequest("/api/auth/register", {
    displayName: "地图作者", email: "maps@example.com", password: "a-safe-password-123",
  }), env);
  const cookie = registration.headers.get("set-cookie").split(";", 1)[0];
  const issued = await worker.fetch(jsonRequest("/api/auth/publish-token", {}, { cookie }), env);
  const accessToken = (await issued.json()).token;
  const keys = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
  const spki = new Uint8Array(await crypto.subtle.exportKey("spki", keys.publicKey));
  const pemBody = Buffer.from(spki).toString("base64").match(/.{1,64}/g).join("\n");
  const publicPem = `-----BEGIN PUBLIC KEY-----\n${pemBody}\n-----END PUBLIC KEY-----`;
  const keyId = Buffer.from(await crypto.subtle.digest("SHA-256", spki)).toString("hex").toUpperCase();
  const packageBytes = new TextEncoder().encode("encrypted-idvm-test");
  const packageHash = Buffer.from(await crypto.subtle.digest("SHA-256", packageBytes)).toString("hex").toUpperCase();
  const publicationId = "11111111-2222-4333-8444-555555555555";
  const payload = {
    schemaVersion: 1, publicationId, publisherHandle: "@internal", publisherDisplayName: "地图作者", publisherKeyId: keyId,
    version: "20260831120000-ABCDEF123456", publishedAtUtc: new Date().toISOString(),
    scope: "current-class", intendedForOfficialWebsite: true,
    packageUri: "maps-test.idvm.secure", encryptedLength: packageBytes.length,
    encryptedSha256: packageHash, plaintextLength: 1, plaintextSha256: "A".repeat(64),
  };
  const payloadBytes = new TextEncoder().encode(JSON.stringify(payload));
  const signature = new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, keys.privateKey, payloadBytes));
  const envelope = { schemaVersion: 1, payload: Buffer.from(payloadBytes).toString("base64"),
    signature: Buffer.from(signature).toString("base64"), publisherPublicKeyPem: publicPem };
  const form = new FormData();
  form.append("feed", new Blob([JSON.stringify(envelope)], { type: "application/json" }), "feed.json");
  form.append("package", new Blob([packageBytes]), payload.packageUri);
  form.append("contentKey", "A".repeat(43));
  form.append("name", "月亮河公园");
  form.append("cover", new Blob([new Uint8Array([137, 80, 78, 71])], { type: "image/png" }), "cover.png");
  const response = await worker.fetch(new Request("https://community.idvb.test/api/maps/publish", {
    method: "POST", headers: { authorization: `Bearer ${accessToken}` }, body: form,
  }), env);
  assert.equal(response.status, 201);
  const result = await response.json();
  assert.equal(result.publisherName, "地图作者");
  assert.match(result.subscriptionLink, /^idvb-sub:\/\/v1\?/);
  assert.deepEqual([...env.bucketObjects.keys()], [
    `community/${publicationId}/maps-test.idvm.secure`, `community/${publicationId}/cover`,
    `community/${publicationId}/feed.json`,
  ]);
  assert.equal(env.publications.size, 1);
});

test("map publication accepts dotnet multipart names without quotes", async () => {
  const boundary = "idvb-dotnet-boundary";
  const body = new TextEncoder().encode(
    `--${boundary}\r\nContent-Disposition: form-data; name=feed; filename=feed.json\r\n\r\n{}\r\n`
    + `--${boundary}\r\nContent-Disposition: form-data; name=contentKey\r\n\r\n${"A".repeat(43)}\r\n`
    + `--${boundary}--\r\n`);
  const form = await publicationFormDataForTest(new Request("https://community.idvb.test/api/maps/publish", {
    method: "POST", headers: { "content-type": `multipart/form-data; boundary=${boundary}` }, body,
  }));
  assert.equal((await form.get("feed").text()), "{}");
  assert.equal(form.get("contentKey"), "A".repeat(43));
});

test("Android feedback accepts anonymous text and optional attachments without creating an account", async () => {
  const env = createEnvironment();
  env.FEEDBACK_RATE_LIMITER = { limit: async () => ({ success: true }) };
  for (const attachments of [false, true]) {
    const form = new FormData();
    form.set("description", "安卓识别问题反馈");
    form.set("clientVersion", "Android v0.1.0-unstable");
    if (attachments) {
      form.set("logs", new Blob(["PK-logs"]), "logs.zip");
      form.set("diagnostics", new Blob(["PK-diagnostics"]), "diagnostics.zip");
    }
    const response = await worker.fetch(new Request("https://community.idvb.test/api/android/feedback", {
      method: "POST", body: form,
    }), env);
    assert.equal(response.status, 201);
    const record = env.feedbacks.get((await response.json()).feedbackId);
    assert.equal(record.user_id, null);
    assert.equal(record.client_version, "Android v0.1.0-unstable");
    assert.equal(record.has_logs, Number(attachments));
    assert.equal(record.has_diagnostics, Number(attachments));
  }
  assert.equal(env.users.size, 0);
});

test("Android feedback validates content and requires an active rate limiter", async () => {
  const env = createEnvironment();
  delete env.FEEDBACK_RATE_LIMITER;
  const submit = (description, headers = {}) => {
    const form = new FormData();
    form.set("description", description);
    return worker.fetch(new Request("https://community.idvb.test/api/android/feedback", {
      method: "POST", body: form, headers,
    }), env);
  };
  assert.equal((await submit("安卓识别问题反馈")).status, 503);
  env.FEEDBACK_RATE_LIMITER = { limit: async () => ({ success: false }) };
  assert.equal((await submit("安卓识别问题反馈")).status, 429);
  env.FEEDBACK_RATE_LIMITER = { limit: async () => ({ success: true }) };
  assert.equal((await submit("一二三四五")).status, 400);
  assert.equal((await submit("a".repeat(4001))).status, 400);
  assert.equal((await submit("安卓识别问题反馈", { "content-length": String(132 * 1024 * 1024) })).status, 413);
  assert.equal(env.feedbacks.size, 0);
});

test("feedback submission requires guest contact and validates weighted length", async () => {
  const env = createEnvironment();
  // 1. 未登录且未提供 QQ 号时拒绝提交。
  const unauthForm = new FormData();
  unauthForm.set("description", "这是一个测试反馈");
  const unauthRes = await worker.fetch(new Request("https://community.idvb.test/api/feedback", {
    method: "POST", body: unauthForm,
  }), env);
  assert.equal(unauthRes.status, 400);
  assert.equal((await unauthRes.json()).error, "invalid_contact_qq");

  // 2. 注册并登录用户
  const regRes = await worker.fetch(new Request("https://community.idvb.test/api/auth/register", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: "tester@idvb.test", displayName: "反馈测试者", password: "Password1234!", turnstileToken: "pass" }),
  }), env);
  const cookie = regRes.headers.get("set-cookie");

  // 3. 描述字符数小于等于 10 拦截（5个汉字=10字符）
  const shortForm = new FormData();
  shortForm.set("description", "一二三四五");
  const shortRes = await worker.fetch(new Request("https://community.idvb.test/api/feedback", {
    method: "POST", headers: { cookie }, body: shortForm,
  }), env);
  assert.equal(shortRes.status, 400);

  // 4. 合法描述提交成功（6个汉字=12字符 > 10）
  const validForm = new FormData();
  validForm.set("description", "一二三四五六");
  validForm.set("clientVersion", "b01.4-26.09.17");
  validForm.set("logs", new Blob([new Uint8Array([80, 75, 3, 4])], { type: "application/zip" }), "logs.zip");
  const validRes = await worker.fetch(new Request("https://community.idvb.test/api/feedback", {
    method: "POST", headers: { cookie }, body: validForm,
  }), env);
  assert.equal(validRes.status, 201);
  const validJson = await validRes.json();
  assert.equal(validJson.success, true);
  assert.equal(env.feedbacks.size, 1);
});

test("feedback accepts desktop multipart field names without quotes", async () => {
  const env = createEnvironment();
  const registration = await worker.fetch(new Request("https://community.idvb.test/api/auth/register", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: "desktop-feedback@idvb.test", displayName: "桌面反馈", password: "Password1234!", turnstileToken: "pass" }),
  }), env);
  const cookie = registration.headers.get("set-cookie");
  const boundary = "idvb-dotnet-feedback-boundary";
  const body = new TextEncoder().encode(
    `--${boundary}\r\nContent-Disposition: form-data; name=description\r\n\r\n桌面客户端反馈请求\r\n`
    + `--${boundary}\r\nContent-Disposition: form-data; name=clientVersion\r\n\r\nb01.4-test\r\n`
    + `--${boundary}\r\nContent-Disposition: form-data; name=logs; filename=logs.zip\r\nContent-Type: application/zip\r\n\r\nPK-test\r\n`
    + `--${boundary}--\r\n`);
  const response = await worker.fetch(new Request("https://community.idvb.test/api/feedback", {
    method: "POST", headers: { cookie, "content-type": `multipart/form-data; boundary=${boundary}` }, body,
  }), env);
  assert.equal(response.status, 201);
  const record = env.feedbacks.get((await response.json()).feedbackId);
  assert.equal(record.description, "桌面客户端反馈请求");
  assert.equal(record.has_logs, 1);
  assert.equal(record.client_version, "b01.4-test");
});

test("builder can view all users and certify regular users without promoting to builder", async () => {
  const env = createEnvironment();
  // 创建普通用户 A
  const regA = await worker.fetch(new Request("https://community.idvb.test/api/auth/register", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: "userA@idvb.test", displayName: "用户A", password: "Password1234!", turnstileToken: "pass" }),
  }), env);
  const cookieA = regA.headers.get("set-cookie");
  const userAId = (await regA.json()).user.id;

  // 普通用户访问建设者后台被 403 拦截
  const forbidRes = await worker.fetch(new Request("https://community.idvb.test/api/builder/users", {
    method: "GET", headers: { cookie: cookieA },
  }), env);
  assert.equal(forbidRes.status, 403);

  // 创建建设者用户 B
  const regB = await worker.fetch(new Request("https://community.idvb.test/api/auth/register", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: "builder@idvb.test", displayName: "建设者B", password: "Password1234!", turnstileToken: "pass" }),
  }), env);
  const cookieB = regB.headers.get("set-cookie");
  const userBId = (await regB.json()).user.id;
  // 数据库手动赋予 B 建设者身份
  env.users.get(userBId).is_builder = 1;

  // 建设者 B 访问全部用户列表
  const usersRes = await worker.fetch(new Request("https://community.idvb.test/api/builder/users", {
    method: "GET", headers: { cookie: cookieB },
  }), env);
  assert.equal(usersRes.status, 200);
  const usersJson = await usersRes.json();
  assert.equal(usersJson.users.length, 2);
  assert.equal(usersJson.users.find((user) => user.id === userAId).displayName, "用户A");
  assert.equal(usersJson.users.find((user) => user.id === userAId).isOfficial, false);

  // 建设者 B 将普通用户 A 升级为“已认证用户”
  const certRes = await worker.fetch(new Request(`https://community.idvb.test/api/builder/users/${userAId}/certify`, {
    method: "POST", headers: { cookie: cookieB, "content-type": "application/json" },
    body: JSON.stringify({ isOfficial: true }),
  }), env);
  assert.equal(certRes.status, 200);
  const certJson = await certRes.json();
  assert.equal(certJson.user.isOfficial, true);
  assert.equal(certJson.user.isBuilder, false); // 绝不能升级成建设者！

  // 再次检查数据库，确认 A 的 is_official 为 1，is_builder 仍为 0
  const userARecord = env.users.get(userAId);
  assert.equal(userARecord.is_official, 1);
  assert.equal(Boolean(userARecord.is_builder), false);

  // 重新获取列表模拟刷新页面；认证标志必须来自持久记录。
  const refreshedRes = await worker.fetch(new Request("https://community.idvb.test/api/builder/users", {
    method: "GET", headers: { cookie: cookieB },
  }), env);
  const refreshedUsers = (await refreshedRes.json()).users;
  assert.equal(refreshedUsers.find((user) => user.id === userAId).isOfficial, true);
});

test("only a signed-in builder can pull feedbacks and download attachments", async () => {
  const env = createEnvironment();
  const unauthenticated = await worker.fetch(new Request("https://community.idvb.test/api/builder/feedbacks"), env);
  assert.equal(unauthenticated.status, 401);
  assert.equal((await unauthenticated.json()).error, "login_required");

  // 普通账户和仅官方认证账户都不是 Builder，不能拉取 Feedback。
  const regOfficial = await worker.fetch(new Request("https://community.idvb.test/api/auth/register", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: "official@idvb.test", displayName: "官方认证用户", password: "Password1234!", turnstileToken: "pass" }),
  }), env);
  const cookieOfficial = regOfficial.headers.get("set-cookie");
  const officialId = (await regOfficial.json()).user.id;

  const regularList = await worker.fetch(new Request("https://community.idvb.test/api/builder/feedbacks", {
    method: "GET", headers: { cookie: cookieOfficial },
  }), env);
  assert.equal(regularList.status, 403);
  assert.equal((await regularList.json()).error, "builder_required");

  env.users.get(officialId).is_official = 1;
  const officialList = await worker.fetch(new Request("https://community.idvb.test/api/builder/feedbacks", {
    method: "GET", headers: { cookie: cookieOfficial },
  }), env);
  assert.equal(officialList.status, 403);
  assert.equal((await officialList.json()).error, "builder_required");

  // 建设者用户登录
  const regBuilder = await worker.fetch(new Request("https://community.idvb.test/api/auth/register", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: "admin_builder@idvb.test", displayName: "管理员建设者", password: "Password1234!", turnstileToken: "pass" }),
  }), env);
  const cookieBuilder = regBuilder.headers.get("set-cookie");
  const builderId = (await regBuilder.json()).user.id;
  env.users.get(builderId).is_builder = 1;

  // 提交一个带有 logs 的反馈
  const form = new FormData();
  form.set("description", "遇到了一个严重的对齐错误，请排查");
  form.set("logs", new Blob([new TextEncoder().encode("log content")], { type: "application/zip" }), "logs.zip");
  const postRes = await worker.fetch(new Request("https://community.idvb.test/api/feedback", {
    method: "POST", headers: { cookie: cookieBuilder }, body: form,
  }), env);
  assert.equal(postRes.status, 201);
  const feedbackId = (await postRes.json()).feedbackId;

  // 建设者查看反馈列表
  const listRes = await worker.fetch(new Request("https://community.idvb.test/api/builder/feedbacks", {
    method: "GET", headers: { cookie: cookieBuilder },
  }), env);
  assert.equal(listRes.status, 200);
  const listJson = await listRes.json();
  assert.equal(listJson.feedbacks.length, 1);
  assert.equal(listJson.feedbacks[0].id, feedbackId);
  assert.equal("logs_key" in listJson.feedbacks[0], false);
  assert.equal("diagnostics_key" in listJson.feedbacks[0], false);

  // 发布令牌只代表桌面发布身份，不能替代 Builder 的网页登录会话。
  const tokenRes = await worker.fetch(new Request("https://community.idvb.test/api/auth/publish-token", {
    method: "POST", headers: { cookie: cookieBuilder },
  }), env);
  const publishToken = (await tokenRes.json()).token;
  const tokenOnlyList = await worker.fetch(new Request("https://community.idvb.test/api/builder/feedbacks", {
    method: "GET", headers: { authorization: `Bearer ${publishToken}` },
  }), env);
  assert.equal(tokenOnlyList.status, 401);
  assert.equal((await tokenOnlyList.json()).error, "login_required");

  // 建设者下载日志附件
  const downloadRes = await worker.fetch(new Request(`https://community.idvb.test/api/builder/feedbacks/${feedbackId}/download/logs`, {
    method: "GET", headers: { cookie: cookieBuilder },
  }), env);
  assert.equal(downloadRes.status, 200);
  assert.equal(downloadRes.headers.get("content-type"), "application/zip");
  assert.equal(downloadRes.headers.get("cache-control"), "no-store");
  assert.equal(downloadRes.headers.get("cross-origin-resource-policy"), "same-origin");
  assert.equal(downloadRes.headers.get("x-content-type-options"), "nosniff");
  assert.equal(downloadRes.headers.get("access-control-allow-origin"), null);

  const crossSiteDownload = await worker.fetch(new Request(`https://community.idvb.test/api/builder/feedbacks/${feedbackId}/download/logs`, {
    method: "GET", headers: { cookie: cookieBuilder, "sec-fetch-site": "cross-site" },
  }), env);
  assert.equal(crossSiteDownload.status, 403);
  assert.equal((await crossSiteDownload.json()).error, "cross_site_request");

  const officialDownload = await worker.fetch(new Request(`https://community.idvb.test/api/builder/feedbacks/${feedbackId}/download/logs`, {
    method: "GET", headers: { cookie: cookieOfficial },
  }), env);
  assert.equal(officialDownload.status, 403);
  assert.equal((await officialDownload.json()).error, "builder_required");
});

test("feedback pull authorization ignores caller-controlled role hints and rechecks the current database role", async () => {
  const env = createEnvironment();
  const registration = await worker.fetch(new Request("https://community.idvb.test/api/auth/register", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: "security-audit@idvb.test", displayName: "权限审计", password: "Password1234!", turnstileToken: "pass" }),
  }), env);
  const cookie = registration.headers.get("set-cookie");
  const userId = (await registration.json()).user.id;
  const user = env.users.get(userId);

  const hintedRequest = await worker.fetch(new Request(
    "https://community.idvb.test/api/builder/feedbacks?is_builder=1&isBuilder=true&role=builder",
    {
      headers: {
        cookie,
        "x-idvb-role": "builder",
        "x-user-is-builder": "1",
        "x-http-method-override": "GET",
      },
    },
  ), env);
  assert.equal(hintedRequest.status, 403);
  assert.equal((await hintedRequest.json()).error, "builder_required");

  const bodyClaim = await worker.fetch(new Request("https://community.idvb.test/api/builder/feedbacks", {
    method: "POST",
    headers: { cookie, "content-type": "application/json" },
    body: JSON.stringify({ is_builder: 1, isBuilder: true, role: "builder" }),
  }), env);
  assert.equal(bodyClaim.status, 404);

  const [cookiePair] = cookie.split(";");
  const [cookieName, cookieValue] = cookiePair.split("=");
  const tamperedCookie = await worker.fetch(new Request(
    "https://community.idvb.test/api/builder/feedbacks?is_builder=1",
    { headers: { cookie: `${cookieName}=${cookieValue}tampered` } },
  ), env);
  assert.equal(tamperedCookie.status, 401);
  assert.equal((await tamperedCookie.json()).error, "login_required");

  // 非数据库整数 1 的 truthy 值也必须失败关闭。
  user.is_builder = "1";
  const stringRole = await worker.fetch(new Request("https://community.idvb.test/api/builder/feedbacks", {
    headers: { cookie },
  }), env);
  assert.equal(stringRole.status, 403);

  user.is_builder = 1;
  const builderRequest = await worker.fetch(new Request("https://community.idvb.test/api/builder/feedbacks", {
    headers: { cookie },
  }), env);
  assert.equal(builderRequest.status, 200);
  assert.equal(builderRequest.headers.get("cache-control"), "no-store");
  assert.equal(builderRequest.headers.get("access-control-allow-origin"), null);

  const crossSiteRequest = await worker.fetch(new Request("https://community.idvb.test/api/builder/feedbacks", {
    headers: { cookie, "sec-fetch-site": "cross-site" },
  }), env);
  assert.equal(crossSiteRequest.status, 403);
  assert.equal((await crossSiteRequest.json()).error, "cross_site_request");

  const foreignOriginRequest = await worker.fetch(new Request("https://community.idvb.test/api/builder/feedbacks", {
    headers: { cookie, origin: "https://attacker.idvb.test" },
  }), env);
  assert.equal(foreignOriginRequest.status, 403);
  assert.equal((await foreignOriginRequest.json()).error, "origin_mismatch");

  // 已登录后被撤销 Builder 身份，旧会话必须立即失去权限；is_official 不能兜底。
  user.is_builder = 0;
  user.is_official = 1;
  const downgradedSession = await worker.fetch(new Request(
    "https://community.idvb.test/api/builder/feedbacks?role=builder",
    { headers: { cookie, "x-idvb-role": "builder" } },
  ), env);
  assert.equal(downgradedSession.status, 403);
  assert.equal((await downgradedSession.json()).error, "builder_required");
});

test("builder can view all maps, toggle visibility, and delete maps while non-builder is rejected", async () => {
  const env = createEnvironment();

  // 1. 注册普通用户
  const regNormal = await worker.fetch(new Request("https://community.idvb.test/api/auth/register", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: "user@idvb.test", displayName: "普通用户", password: "Password1234!", turnstileToken: "pass" }),
  }), env);
  const cookieNormal = regNormal.headers.get("set-cookie");

  // 2. 注册建设者用户
  const regBuilder = await worker.fetch(new Request("https://community.idvb.test/api/auth/register", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: "builder@idvb.test", displayName: "地图守护者", password: "Password1234!", turnstileToken: "pass" }),
  }), env);
  const cookieBuilder = regBuilder.headers.get("set-cookie");
  const builderId = (await regBuilder.json()).user.id;
  env.users.get(builderId).is_builder = 1;

  // 3. 初始植入一张测试地图
  const mapId = "11111111-2222-3333-4444-555555555555";
  const feedKey = `community/${mapId}/feed.json`;
  const packageKey = `community/${mapId}/test.idvm.secure`;
  const coverKey = `community/${mapId}/cover`;
  env.bucketObjects.set(feedKey, new Uint8Array([1, 2, 3]));
  env.bucketObjects.set(packageKey, new Uint8Array([4, 5, 6]));
  env.bucketObjects.set(coverKey, new Uint8Array([7, 8, 9]));
  const mapItem = {
    id: mapId,
    user_id: builderId,
    display_name: "地图守护者",
    name: "月亮河公园",
    version: "1.0.0",
    feed_key: feedKey,
    package_key: packageKey,
    cover_key: coverKey,
    content_key: "ck_test",
    publisher_key_id: "pk_test",
    created_at: new Date().toISOString(),
    updated_at: new Date().toISOString(),
    is_hidden: 0,
  };
  env.publications.set(mapId, mapItem);

  // 4. 普通用户尝试访问建设者地图管理 -> 403 Forbidden
  const normalGetRes = await worker.fetch(new Request("https://community.idvb.test/api/builder/maps", {
    method: "GET", headers: { cookie: cookieNormal },
  }), env);
  assert.equal(normalGetRes.status, 403);

  // 5. 建设者访问建设者地图管理 -> 200 OK，包含该地图
  const builderGetRes = await worker.fetch(new Request("https://community.idvb.test/api/builder/maps", {
    method: "GET", headers: { cookie: cookieBuilder },
  }), env);
  assert.equal(builderGetRes.status, 200);
  const builderGetJson = await builderGetRes.json();
  assert.equal(builderGetJson.maps.length, 1);
  assert.equal(builderGetJson.maps[0].id, mapId);
  assert.equal(builderGetJson.maps[0].isHidden, false);

  // 6. 确认前台公共列表能够看到该地图
  const publicRes1 = await worker.fetch(new Request("https://community.idvb.test/api/maps"), env);
  assert.equal(publicRes1.status, 200);
  const publicJson1 = await publicRes1.json();
  assert.equal(publicJson1.publications.length, 1);

  // 7. 建设者将地图设置为“隐藏”
  const hideRes = await worker.fetch(new Request(`https://community.idvb.test/api/builder/maps/${mapId}/visibility`, {
    method: "POST", headers: { cookie: cookieBuilder, "content-type": "application/json" },
    body: JSON.stringify({ hidden: true }),
  }), env);
  assert.equal(hideRes.status, 200);
  const hideJson = await hideRes.json();
  assert.equal(hideJson.isHidden, true);
  assert.equal(mapItem.is_hidden, 1);

  // 8. 验证前台公共列表不再返回该地图（已被隐藏）
  const publicRes2 = await worker.fetch(new Request("https://community.idvb.test/api/maps"), env);
  assert.equal(publicRes2.status, 200);
  const publicJson2 = await publicRes2.json();
  assert.equal(publicJson2.publications.length, 0);

  // 9. 验证建设者后台仍然可以看到该隐藏地图（且 isHidden: true）
  const builderGetRes2 = await worker.fetch(new Request("https://community.idvb.test/api/builder/maps", {
    method: "GET", headers: { cookie: cookieBuilder },
  }), env);
  const builderGetJson2 = await builderGetRes2.json();
  assert.equal(builderGetJson2.maps.length, 1);
  assert.equal(builderGetJson2.maps[0].isHidden, true);

  // 10. 建设者将地图“取消隐藏”
  const unhideRes = await worker.fetch(new Request(`https://community.idvb.test/api/builder/maps/${mapId}/visibility`, {
    method: "POST", headers: { cookie: cookieBuilder, "content-type": "application/json" },
    body: JSON.stringify({ hidden: false }),
  }), env);
  assert.equal(unhideRes.status, 200);
  assert.equal((await unhideRes.json()).isHidden, false);

  // 11. 验证前台公共列表重新恢复该地图
  const publicRes3 = await worker.fetch(new Request("https://community.idvb.test/api/maps"), env);
  assert.equal((await publicRes3.json()).publications.length, 1);

  // 12. 普通用户尝试删除地图 -> 403 Forbidden
  const normalDelRes = await worker.fetch(new Request(`https://community.idvb.test/api/builder/maps/${mapId}`, {
    method: "DELETE", headers: { cookie: cookieNormal },
  }), env);
  assert.equal(normalDelRes.status, 403);
  assert.equal(env.publications.has(mapId), true);

  // 13. 建设者删除地图 -> 200 OK
  const builderDelRes = await worker.fetch(new Request(`https://community.idvb.test/api/builder/maps/${mapId}`, {
    method: "DELETE", headers: { cookie: cookieBuilder },
  }), env);
  assert.equal(builderDelRes.status, 200);
  assert.equal(env.publications.has(mapId), false);
  // 确认 R2 中的对象已被级联清理
  assert.equal(env.bucketObjects.has(feedKey), false);
  assert.equal(env.bucketObjects.has(packageKey), false);
  assert.equal(env.bucketObjects.has(coverKey), false);

  // 14. 建设者后台与公共列表均为空
  const builderGetRes3 = await worker.fetch(new Request("https://community.idvb.test/api/builder/maps", {
    method: "GET", headers: { cookie: cookieBuilder },
  }), env);
  assert.equal((await builderGetRes3.json()).maps.length, 0);
});

test("announcements: 权限控制、发布、读取与管理流程", async () => {
  const env = createEnvironment();

  // 1. 准备一个普通用户和一个 builder 用户
  const regNormal = await worker.fetch(new Request("https://community.idvb.test/api/auth/register", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: "user@idvb.test", password: "Password123!", displayName: "NormalUser", turnstileToken: "pass" }),
  }), env);
  const cookieNormal = regNormal.headers.get("set-cookie").split(";")[0];

  const regBuilder = await worker.fetch(new Request("https://community.idvb.test/api/auth/register", {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: "builder@idvb.test", password: "Password123!", displayName: "BuilderUser", turnstileToken: "pass" }),
  }), env);
  const cookieBuilder = regBuilder.headers.get("set-cookie").split(";")[0];

  const builderUser = [...env.users.values()].find((u) => u.email === "builder@idvb.test");
  builderUser.is_builder = 1;

  // 2. 普通用户尝试发布公告 -> 403 Forbidden
  const forbiddenRes = await worker.fetch(new Request("https://community.idvb.test/api/announcements", {
    method: "POST", headers: { cookie: cookieNormal, "content-type": "application/json" },
    body: JSON.stringify({ title: "尝试发布", content: "普通用户内容", category: "notice" }),
  }), env);
  assert.equal(forbiddenRes.status, 403);
  const forbiddenJson = await forbiddenRes.json();
  assert.equal(forbiddenJson.error, "builder_required");

  // 3. Builder 发布更新公告与冷知识
  const createRes1 = await worker.fetch(new Request("https://community.idvb.test/api/announcements", {
    method: "POST", headers: { cookie: cookieBuilder, "content-type": "application/json" },
    body: JSON.stringify({
      title: "v1.6.1 版本更新说明",
      category: "update",
      tag: "v1.6.1",
      summary: "新增全屏大尺寸 Markdown 公告与冷知识系统",
      content: "# v1.6.1\n\n- 新增公告中心\n- 支持 GitHub 风格 Markdown",
      isPinned: 1,
      priority: 10,
    }),
  }), env);
  assert.equal(createRes1.status, 201);
  const createJson1 = await createRes1.json();
  assert.equal(createJson1.success, true);
  assert.equal(createJson1.announcement.title, "v1.6.1 版本更新说明");
  const notice1Id = createJson1.id;

  const createRes2 = await worker.fetch(new Request("https://community.idvb.test/api/announcements", {
    method: "POST", headers: { cookie: cookieBuilder, "content-type": "application/json" },
    body: JSON.stringify({
      title: "冷知识：红教堂侧门速查技巧",
      category: "tips",
      tag: "红教堂",
      summary: "如何在开局 3 秒内确认红教堂侧门位置",
      content: "### 技巧说明\n\n观察小地图红教堂侧门边缘结构...",
      isPinned: 0,
      priority: 5,
    }),
  }), env);
  assert.equal(createRes2.status, 201);
  const createJson2 = await createRes2.json();
  const notice2Id = createJson2.id;

  // 4. 访客公开拉取全部列表
  const listRes = await worker.fetch(new Request("https://community.idvb.test/api/announcements"), env);
  assert.equal(listRes.status, 200);
  const listJson = await listRes.json();
  assert.equal(listJson.announcements.length, 2);
  // 置顶的排在最前面
  assert.equal(listJson.announcements[0].id, notice1Id);

  // 5. 访客按分类拉取 (category=tips)
  const tipsRes = await worker.fetch(new Request("https://community.idvb.test/api/announcements?category=tips"), env);
  assert.equal(tipsRes.status, 200);
  const tipsJson = await tipsRes.json();
  assert.equal(tipsJson.announcements.length, 1);
  assert.equal(tipsJson.announcements[0].id, notice2Id);

  // 6. 访客拉取单篇公告详情
  const detailRes = await worker.fetch(new Request(`https://community.idvb.test/api/announcements/${notice1Id}`), env);
  assert.equal(detailRes.status, 200);
  const detailJson = await detailRes.json();
  assert.equal(detailJson.announcement.title, "v1.6.1 版本更新说明");

  // 7. Builder 更新公告标题
  const updateRes = await worker.fetch(new Request(`https://community.idvb.test/api/announcements/${notice1Id}`, {
    method: "PUT", headers: { cookie: cookieBuilder, "content-type": "application/json" },
    body: JSON.stringify({ title: "v1.6.1 正式发布说明" }),
  }), env);
  assert.equal(updateRes.status, 200);
  assert.equal((await updateRes.json()).success, true);
  assert.equal(env.announcements.get(notice1Id).title, "v1.6.1 正式发布说明");

  // 8. Builder 后台拉取公告管理列表（普通用户 403，Builder 200）
  const bAnnounceForbidden = await worker.fetch(new Request("https://community.idvb.test/api/builder/announcements", {
    method: "GET", headers: { cookie: cookieNormal },
  }), env);
  assert.equal(bAnnounceForbidden.status, 403);

  const bAnnounceRes = await worker.fetch(new Request("https://community.idvb.test/api/builder/announcements", {
    method: "GET", headers: { cookie: cookieBuilder },
  }), env);
  assert.equal(bAnnounceRes.status, 200);
  const bAnnounceJson = await bAnnounceRes.json();
  assert.equal(bAnnounceJson.announcements.length, 2);

  // 9. Builder 删除公告
  const delRes = await worker.fetch(new Request(`https://community.idvb.test/api/announcements/${notice2Id}`, {
    method: "DELETE", headers: { cookie: cookieBuilder },
  }), env);
  assert.equal(delRes.status, 200);
  assert.equal(env.announcements.has(notice2Id), false);
});

test("desktop guest feedback requires QQ and persists contact and attachments", async () => {
  const env = createEnvironment();
  for (const qq of ["", "1234", "012345", "12345a", "１２３４５", "1234567890123"]) {
    const form = new FormData();
    form.set("description", "桌面端未登录反馈测试描述");
    form.set("contactQq", qq);
    const response = await worker.fetch(new Request("https://community.idvb.test/api/feedback", { method: "POST", body: form }), env);
    assert.equal(response.status, 400);
    assert.equal((await response.json()).error, "invalid_contact_qq");
  }
  assert.equal(env.feedbacks.size, 0);
  const form = new FormData();
  form.set("description", "桌面端未登录反馈测试描述");
  form.set("contactQq", " 12345678 ");
  form.set("logs", new File(["sample logs"], "logs.zip", { type: "application/zip" }));
  const response = await worker.fetch(new Request("https://community.idvb.test/api/feedback", { method: "POST", body: form }), env);
  assert.equal(response.status, 201);
  const record = env.feedbacks.get((await response.json()).feedbackId);
  assert.equal(record.user_id, null);
  assert.equal(record.contact_qq, "12345678");
  assert.equal(record.has_logs, 1);
});
