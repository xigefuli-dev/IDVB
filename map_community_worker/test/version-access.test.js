import assert from "node:assert/strict";
import test from "node:test";
import { generateKeyPairSync, createHash, verify } from "node:crypto";
import worker from "../src/index.js";
import { validProductVersion } from "../src/version-access.js";

const pair = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
const token = "test-desktop-session-token";
const tokenHash = createHash("sha256").update(token).digest("base64url");
const nonce = "n".repeat(43);
function environment(role = {}) {
  const policies = new Map([["1.6.6", { version: "1.6.6", enabled: 1, message: "" }]]);
  const user = { id: "user-1", is_official: 0, is_builder: 0, ...role };
  const audit = [];
  const env = { VERSION_ACCESS_PRIVATE_KEY: pair.privateKey.export({ type: "pkcs8", format: "der" }).toString("base64"), policies, user, audit };
  env.COMMUNITY_DB = {
    prepare(sql) {
      return { bind(...values) { this.values = values; return this; },
        async first() {
          if (sql.includes("FROM publish_tokens") || sql.includes("FROM auth_sessions"))
            return this.values[0] === tokenHash && !env.revoked ? user : null;
          if (sql.includes("FROM software_versions")) return policies.get(this.values[0]) || null;
          throw new Error(sql);
        },
        async all() { assert.match(sql, /FROM software_versions/); return { results: [...policies.values()] }; },
        async run() {
          const [version, enabled, message, updated_at, updated_by] = this.values;
          if (sql.includes("INSERT INTO software_versions")) policies.set(version, { version, enabled, message, updated_at, updated_by });
          else if (sql.includes("INSERT INTO software_version_audit")) audit.push(this.values);
          else throw new Error(sql);
          return { success: true };
        }
      };
    },
    async batch(items) { return Promise.all(items.map(item => item.run())); }
  };
  return env;
}
function request(path, body, { cookie = false, auth = true, method = "POST", origin } = {}) {
  return new Request(`https://community.idvb.test${path}`, { method,
    headers: { "content-type": "application/json", ...(auth ? cookie ? { cookie: `idvb_community_session=${token}` } : { authorization: `Bearer ${token}` } : {}), ...(origin ? { origin } : {}) },
    ...(body ? { body: JSON.stringify(body) } : {}) });
}
async function check(env, version = "1.6.6", extra = {}) {
  const response = await worker.fetch(request("/api/client/version-access", { version, nonce, ...extra }), env);
  assert.equal(response.status, 200);
  const envelope = await response.json();
  const bytes = Buffer.from(envelope.payload, "base64");
  assert.equal(verify("sha256", bytes, { key: pair.publicKey, dsaEncoding: "ieee-p1363" }, Buffer.from(envelope.signature, "base64")), true);
  const claims = JSON.parse(bytes);
  assert.equal(claims.tokenHash, tokenHash); assert.equal(claims.nonce, nonce);
  assert.equal(claims.version, version); assert.equal(claims.expiresAt - claims.issuedAt, 120);
  return claims;
}
test("ordinary login follows allowlist; disable and re-enable take effect; missing versions fail closed", async () => {
  const env = environment();
  assert.equal((await check(env)).allowed, true);
  env.policies.get("1.6.6").enabled = 0;
  assert.equal((await check(env)).allowed, false);
  env.policies.get("1.6.6").enabled = 1;
  assert.equal((await check(env)).allowed, true);
  assert.equal((await check(env, "1.6.7")).allowed, false);
  assert.equal((await check(env, "1.6.5")).allowed, false);
  assert.equal((await check(env, "1.6.7", { isOfficial: true, offlineExempt: true })).allowed, false);
});
test("official and builder records receive signed offline grants even for disabled versions", async () => {
  for (const role of [{ is_official: 1 }, { is_builder: 1 }]) {
    const claims = await check(environment(role), "1.6.7");
    assert.equal(claims.allowed, true); assert.equal(claims.offlineExempt, true);
  }
});
test("anonymous and revoked desktop tokens cannot check versions", async () => {
  const env = environment();
  assert.equal((await worker.fetch(request("/api/client/version-access", { version: "1.6.6", nonce }, { auth: false }), env)).status, 401);
  env.revoked = true;
  assert.equal((await worker.fetch(request("/api/client/version-access", { version: "1.6.6", nonce }), env)).status, 401);
});
test("only signed-in builders can manage policy; writes produce audit entries", async () => {
  const env = environment({ is_builder: 1 });
  const body = { version: "1.6.6", enabled: false, message: "升级到新版" };
  assert.equal((await worker.fetch(request("/api/builder/versions", body, { method: "PUT" }), env)).status, 401);
  assert.equal((await worker.fetch(request("/api/builder/versions", body, { cookie: true, method: "PUT", origin: "https://other.test" }), env)).status, 403);
  assert.equal(env.audit.length, 0);
  assert.equal((await worker.fetch(request("/api/builder/versions", body, { cookie: true, method: "PUT" }), env)).status, 200);
  assert.equal(env.audit.length, 1);
  env.user.is_builder = 0;
  assert.equal((await check(env)).allowed, false);
  env.user.is_builder = 1;
  const listed = await worker.fetch(request("/api/builder/versions", null, { cookie: true, method: "GET" }), env);
  assert.equal((await listed.json()).versions[0].enabled, 0);
  env.user.is_builder = 0; env.user.is_official = 1;
  assert.equal((await worker.fetch(request("/api/builder/versions", body, { cookie: true, method: "PUT" }), env)).status, 403);
});
test("malformed versions and policy fields rejected without a write", async () => {
  const env = environment({ is_builder: 1 });
  for (const version of ["01.6.6", "1.6", "1.6.6-beta", "1.6.6/", "99999.0.0", null]) {
    assert.equal(validProductVersion(version), false);
    assert.equal((await worker.fetch(request("/api/client/version-access", { version, nonce }), env)).status, 400);
  }
  assert.equal((await worker.fetch(request("/api/builder/versions", { version: "1.6.6", enabled: "false", message: "" }, { cookie: true, method: "PUT" }), env)).status, 400);
  assert.equal(env.audit.length, 0);
});
