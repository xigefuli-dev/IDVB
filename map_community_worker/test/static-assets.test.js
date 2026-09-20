import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

test("Turnstile can load and is rendered explicitly", () => {
  const html = readFileSync(new URL("../public/index.html", import.meta.url), "utf8");
  const script = readFileSync(new URL("../public/app.js", import.meta.url), "utf8");
  const headers = readFileSync(new URL("../public/_headers", import.meta.url), "utf8");

  assert.match(html, /api\.js\?render=explicit/);
  assert.match(script, /window\.turnstile\.render/);
  assert.match(headers, /script-src[^;]+https:\/\/challenges\.cloudflare\.com/);
  assert.match(headers, /frame-src https:\/\/challenges\.cloudflare\.com/);
});

test("avatar FormData lets the browser supply its multipart boundary", () => {
  const script = readFileSync(new URL("../public/app.js", import.meta.url), "utf8");
  assert.match(script, /!\(options\.body instanceof FormData\)/);
  assert.doesNotMatch(script, /headers: \{ "content-type": "application\/json"/);
});

test("subscription cards always copy their links", () => {
  const html = readFileSync(new URL("../public/index.html", import.meta.url), "utf8");
  const script = readFileSync(new URL("../public/app.js", import.meta.url), "utf8");
  assert.match(html, /id="copy-toast"/);
  assert.match(script, /navigator\.clipboard\.writeText\(map\.downloadUrl\)/);
  assert.doesNotMatch(script, /subscriptionGuideActive|IDVB-SUBSCRIBE-MAP-GUIDE/);
  assert.match(script, /copy-toast-enter/);
});

test("assets configuration enables run_worker_first and feedback downloads are handled safely", () => {
  const wrangler = readFileSync(new URL("../wrangler.toml", import.meta.url), "utf8");
  const script = readFileSync(new URL("../public/app.js", import.meta.url), "utf8");

  assert.match(wrangler, /run_worker_first\s*=\s*true/);
  assert.match(script, /data-download-feedback/);
  assert.match(script, /downloadFeedbackAttachment/);
});
