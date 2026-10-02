import assert from "node:assert/strict";
import test from "node:test";
import worker from "../src/index.js";

const apk = "android/IDVB-Android-v0.2.0-b00.2-26.09.30.0041-debug.apk";
const old = "android/IDVB-Android-v0.2.0-b00.2-26.09.30.0037-debug.apk";
function fixture() {
  const reads = [];
  return { reads, env: { INSTALLER_BUCKET: {
    async list() { return { truncated: false, objects: [
      { key: old, size: 8, uploaded: new Date("2026-10-01"), etag: '"old"' },
      { key: apk, size: 8, uploaded: new Date("2026-09-30"), etag: '"new"' },
      { key: "feedbacks/private.apk", size: 8, uploaded: new Date("2026-10-02") },
      { key: "android/IDVB-Android-v0.2.0-b00.2-26.09.30.9999-release-unsigned.apk", size: 8, uploaded: new Date("2026-10-02") },
      { key: "android/nested/IDVB-Android-v0.2.0-b00.2-26.09.30.9999-debug.apk", size: 8, uploaded: new Date("2026-10-02") },
    ] }; },
    async get(key, options) { reads.push(key); const all = new Uint8Array(8); return {
      size: 8, httpEtag: '"new"', writeHttpMetadata() {},
      body: options?.range ? all.slice(options.range.offset, options.range.offset + options.range.length) : all,
    }; },
  } } };
}
test("Android downloads select the newest build and exclude private, nested and unsigned files", async () => {
  const { env, reads } = fixture();
  const response = await worker.fetch(new Request("https://download.test/android"), env);
  assert.equal(response.status, 200);
  assert.deepEqual(reads, [apk]);
  assert.equal(response.headers.get("content-type"), "application/vnd.android.package-archive");
  assert.match(response.headers.get("content-disposition"), /0041-debug\.apk/);
  assert.equal((await response.arrayBuffer()).byteLength, 8);
});
test("Android supports HEAD and byte ranges", async () => {
  const { env } = fixture();
  const head = await worker.fetch(new Request("https://download.test/android", { method: "HEAD" }), env);
  assert.equal(head.status, 200);
  assert.equal((await head.arrayBuffer()).byteLength, 0);
  const range = await worker.fetch(new Request("https://download.test/android", { headers: { range: "bytes=2-4" } }), env);
  assert.equal(range.status, 206);
  assert.equal(range.headers.get("content-range"), "bytes 2-4/8");
  assert.equal((await range.arrayBuffer()).byteLength, 3);
});
test("Android returns 404 when no published APK exists", async () => {
  const response = await worker.fetch(new Request("https://download.test/android"), { INSTALLER_BUCKET: { async list() { return { objects: [], truncated: false }; } } });
  assert.equal(response.status, 404);
});
