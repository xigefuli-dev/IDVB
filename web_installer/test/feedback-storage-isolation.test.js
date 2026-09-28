import assert from "node:assert/strict";
import test from "node:test";
import worker from "../src/index.js";

test("public download routes cannot address private feedback objects", async () => {
  let bucketReads = 0;
  const env = {
    INSTALLER_BUCKET: {
      async get() { bucketReads += 1; return null; },
      async head() { bucketReads += 1; return null; },
      async list() { bucketReads += 1; return { objects: [], truncated: false }; },
    },
  };
  const feedbackKey = "feedbacks/2026-09-28/00000000-0000-0000-0000-000000000000/logs.zip";
  const attempts = [
    { url: `https://download.idvb.test/${feedbackKey}`, status: 404 },
    { url: `https://download.idvb.test/maps/${encodeURIComponent(feedbackKey)}`, status: 404 },
    { url: `https://download.idvb.test/?file=${encodeURIComponent(feedbackKey)}`, status: 302 },
    { url: `https://download.idvb.test/updates/win-x64-stable/${encodeURIComponent(feedbackKey)}`, status: 404 },
  ];

  for (const { url, status } of attempts) {
    const response = await worker.fetch(new Request(url), env);
    assert.equal(response.status, status, url);
    if (status === 302) assert.equal(response.headers.get("location"), "https://idvb.xgflee.com/download");
  }
  assert.equal(bucketReads, 0, "rejected feedback paths must not reach the shared R2 bucket");
});
