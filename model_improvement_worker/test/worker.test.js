import assert from "node:assert/strict";
import test from "node:test";
import worker from "../src/index.js";

const ADMIN_TOKEN = "a".repeat(64);

function createEnvironment() {
  const objects = new Map();
  return {
    ADMIN_TOKEN,
    UPLOAD_RATE_LIMITER: { limit: async () => ({ success: true }) },
    TRAINING_BUCKET: {
      head: async (key) => objects.get(key) || null,
      put: async (key, body, options) => {
        const bytes = new Uint8Array(await new Response(body).arrayBuffer());
        objects.set(key, {
          key,
          size: bytes.length,
          uploaded: new Date(),
          customMetadata: options.customMetadata,
          httpMetadata: options.httpMetadata,
          writeHttpMetadata() {},
          body: new Response(bytes).body,
        });
      },
      get: async (key) => objects.get(key) || null,
      list: async () => ({ objects: [...objects.values()], truncated: false }),
    },
    objects,
  };
}

test("public callers cannot list or download packages", async () => {
  const response = await worker.fetch(new Request(
    "https://idvb.xgflee.com/model-improvement-admin/"), createEnvironment());
  assert.equal(response.status, 200);
  assert.match(await response.text(), /管理令牌/);
});

test("upload rejects incomplete metadata", async () => {
  const response = await worker.fetch(new Request(
    "https://idvb.xgflee.com/api/model-improvement/training-packages", {
      method: "POST",
      headers: { "content-type": "application/zip", "content-length": "3" },
      body: "zip",
    }), createEnvironment());
  assert.equal(response.status, 400);
});

test("valid upload is stored and returns a matching receipt", async () => {
  const env = createEnvironment();
  const datasetFingerprint = "b".repeat(64);
  const packageSha256 = "c".repeat(64);
  const response = await worker.fetch(new Request(
    "https://idvb.xgflee.com/api/model-improvement/training-packages", {
      method: "POST",
      headers: {
        "content-type": "application/zip",
        "content-length": "3",
        "x-idvb-build-version": "b01.5-26.08.31.0001",
        "x-idvb-dataset-fingerprint": datasetFingerprint,
        "x-idvb-package-sha256": packageSha256,
        "x-idvb-sample-count": "4",
      },
      body: "zip",
    }), env);
  assert.equal(response.status, 201);
  assert.equal(env.objects.size, 1);
  assert.deepEqual(await response.json(), {
    accepted: true,
    duplicate: false,
    datasetFingerprint,
    packageSha256,
  });
});

test("correct token creates a private admin session", async () => {
  const env = createEnvironment();
  const response = await worker.fetch(new Request(
    "https://idvb.xgflee.com/model-improvement-admin/session", {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ token: ADMIN_TOKEN }),
    }), env);
  assert.equal(response.status, 303);
  const cookie = response.headers.get("set-cookie");
  assert.match(cookie, /HttpOnly; Secure; SameSite=Strict/);

  const list = await worker.fetch(new Request(
    "https://idvb.xgflee.com/model-improvement-admin/", {
      headers: { cookie: cookie.split(";", 1)[0] },
    }), env);
  assert.equal(list.status, 200);
  assert.match(await list.text(), /私有训练包/);
});
