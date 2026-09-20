const DOWNLOAD_ROUTES = new Map([
  ["/installer", "installer"],
  ["/idvb-setup", "installer"],
  ["/idvm", "idvm"],
]);

const DOWNLOAD_PAGE = "https://idvb.xgflee.com/download";

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === "/" || url.pathname === "/latest") {
      return Response.redirect(DOWNLOAD_PAGE, 302);
    }
    const mapCatalogRequested = url.pathname === "/api/maps";
    const mapObjectKey = getMapObjectKey(url);
    const kind = getDownloadKind(url);
    const updateObjectKey = getUpdateObjectKey(url);
    const guideVideoObjectKey = getGuideVideoObjectKey(url);

    if (!kind && !updateObjectKey && !guideVideoObjectKey && !mapCatalogRequested && !mapObjectKey) {
      return new Response(
        "Identity Vision Bridge download service\n\n" +
          "GET / or /installer  -> latest IDVB-Setup\n" +
          "GET /idvm            -> latest IDVM\n" +
          "GET /api/maps        -> available IDVM map packages\n" +
          "GET /maps/{key}      -> selected IDVM map package\n" +
          "GET /updates/{channel}/{file} -> signed Velopack feed asset\n",
        {
          status: 404,
          headers: {
            "content-type": "text/plain; charset=utf-8",
            "cache-control": "no-store",
          },
        },
      );
    }

    if (request.method !== "GET" && request.method !== "HEAD") {
      return new Response("Method Not Allowed", {
        status: 405,
        headers: { allow: "GET, HEAD" },
      });
    }

    if (updateObjectKey) {
      return updateObjectResponse(request, env.INSTALLER_BUCKET, updateObjectKey);
    }

    if (guideVideoObjectKey) {
      return updateObjectResponse(request, env.INSTALLER_BUCKET, guideVideoObjectKey);
    }

    if (mapCatalogRequested) {
      return mapCatalogResponse(request, env.INSTALLER_BUCKET);
    }

    if (mapObjectKey) {
      return mapPackageResponse(request, env.INSTALLER_BUCKET, mapObjectKey);
    }

    if (kind === "bundle") {
      return latestBundleResponse(request);
    }

    const objects = await listObjects(env.INSTALLER_BUCKET);
    const object = selectLatest(objects, kind);

    if (!object) {
      return new Response(
        kind === "installer"
          ? "No IDVB-Setup installer is available."
          : "No IDVM file is available.",
        {
          status: 404,
          headers: {
            "content-type": "text/plain; charset=utf-8",
            "cache-control": "no-store",
          },
        },
      );
    }

    if (request.headers.get("if-none-match") === object.etag) {
      return new Response(null, {
        status: 304,
        headers: { etag: object.etag, "cache-control": "no-store" },
      });
    }

    const range = parseRange(request.headers.get("range"), object.size);
    if (range === "unsatisfiable") {
      return new Response(null, {
        status: 416,
        headers: {
          "content-range": `bytes */${object.size}`,
          "cache-control": "no-store",
        },
      });
    }

    const bodyObject = await env.INSTALLER_BUCKET.get(
      object.key,
      range ? { range } : undefined,
    );

    if (!bodyObject) {
      return new Response("The selected R2 object is no longer available.", {
        status: 404,
        headers: { "cache-control": "no-store" },
      });
    }

    const headers = new Headers();
    bodyObject.writeHttpMetadata(headers);
    headers.set("content-type", "application/octet-stream");
    headers.set(
      "content-disposition",
      `attachment; filename="${safeFilename(object.key)}"`,
    );
    headers.set("cache-control", "no-store");
    headers.set("etag", bodyObject.httpEtag || object.etag);
    headers.set("accept-ranges", "bytes");
    headers.set("content-length", String(bodyObject.size ?? object.size));
    headers.set("access-control-allow-origin", "*");

    if (range) {
      const offset = range.offset;
      const length = range.length ?? object.size - offset;
      headers.set("content-range", `bytes ${offset}-${offset + length - 1}/${object.size}`);
      headers.set("content-length", String(length));
    }

    return new Response(request.method === "HEAD" ? null : bodyObject.body, {
      status: range ? 206 : 200,
      headers,
    });
  },
};

function getMapObjectKey(url) {
  if (!url.pathname.startsWith("/maps/")) return null;
  try {
    const key = decodeURIComponent(url.pathname.slice(6));
    return key && key.toLowerCase().endsWith(".idvm") && !key.includes("..")
      ? key
      : null;
  } catch {
    return null;
  }
}

async function mapCatalogResponse(request, bucket) {
  const headers = new Headers({
    "content-type": "application/json; charset=utf-8",
    "cache-control": "no-store",
    "access-control-allow-origin": "https://idvb.xgflee.com",
    "x-content-type-options": "nosniff",
  });
  if (request.method === "HEAD") return new Response(null, { status: 200, headers });

  const objects = (await listObjects(bucket))
    .filter((object) => object.key.toLowerCase().endsWith(".idvm"))
    .sort((left, right) => new Date(right.uploaded) - new Date(left.uploaded));
  const maps = objects.map((object) => ({
    name: safeFilename(object.key).replace(/\.idvm$/i, ""),
    filename: safeFilename(object.key),
    size: object.size,
    uploaded: object.uploaded,
    downloadUrl: `/maps/${encodeURIComponent(object.key)}`,
  }));
  return Response.json({ maps }, { headers });
}

async function mapPackageResponse(request, bucket, key) {
  const metadata = await bucket.head(key);
  if (!metadata || !key.toLowerCase().endsWith(".idvm")) {
    return new Response("Map package not found.", { status: 404 });
  }
  const object = request.method === "HEAD" ? metadata : await bucket.get(key);
  if (!object) return new Response("Map package no longer exists.", { status: 404 });
  const headers = new Headers();
  object.writeHttpMetadata(headers);
  headers.set("content-type", "application/octet-stream");
  headers.set("content-disposition", `attachment; filename="${safeFilename(key)}"`);
  headers.set("content-length", String(metadata.size));
  headers.set("cache-control", "public, max-age=3600");
  headers.set("etag", object.httpEtag || metadata.httpEtag || metadata.etag);
  headers.set("access-control-allow-origin", "*");
  headers.set("x-content-type-options", "nosniff");
  return new Response(request.method === "HEAD" ? null : object.body, { status: 200, headers });
}

function getUpdateObjectKey(url) {
  // This route must be deployed independently from uploading R2 release assets.
  // An older Worker can keep returning the service's generic 404 even while the
  // signed envelope and packages are already present and correct in remote R2.
  // Release completion therefore requires a public-domain envelope readback.
  // Channels are fixed so a request can never escape into unrelated R2 keys.
  // Filenames are leaf names only; encoded slashes and traversal are rejected.
  const match = /^\/updates\/(win-x64-test|win-x64-stable)\/([A-Za-z0-9][A-Za-z0-9._+-]{0,199})$/.exec(
    url.pathname,
  );
  if (!match || match[2].includes("..")) {
    return null;
  }
  return `updates/${match[1]}/${match[2]}`;
}

function getGuideVideoObjectKey(url) {
  // Keep the public media surface deliberately narrow: the onboarding can only
  // request its four immutable tutorial videos, never an arbitrary R2 key.
  const match = /^\/guides\/onboarding\/(vid[1-4]\.mp4)$/.exec(url.pathname);
  return match ? `guides/onboarding/${match[1]}` : null;
}

async function updateObjectResponse(request, bucket, key) {
  const metadata = await bucket.head(key);
  if (!metadata) {
    return new Response("Update asset not found.", {
      status: 404,
      headers: { "cache-control": "no-store" },
    });
  }

  const etag = metadata.httpEtag || metadata.etag;
  if (request.headers.get("if-none-match") === etag) {
    return new Response(null, {
      status: 304,
      headers: { etag, "cache-control": updateCacheControl(key) },
    });
  }

  const range = parseRange(request.headers.get("range"), metadata.size);
  if (range === "unsatisfiable") {
    return new Response(null, {
      status: 416,
      headers: {
        "content-range": `bytes */${metadata.size}`,
        "cache-control": updateCacheControl(key),
      },
    });
  }

  const object = request.method === "HEAD"
    ? metadata
    : await bucket.get(key, range ? { range } : undefined);
  if (!object) {
    return new Response("Update asset no longer exists.", {
      status: 404,
      headers: { "cache-control": "no-store" },
    });
  }

  const headers = new Headers();
  object.writeHttpMetadata(headers);
  headers.set("content-type", updateContentType(key));
  headers.set("cache-control", updateCacheControl(key));
  headers.set("etag", object.httpEtag || etag);
  headers.set("accept-ranges", "bytes");
  headers.set("content-length", String(range?.length ?? metadata.size));
  headers.set("access-control-allow-origin", "*");
  headers.set("x-content-type-options", "nosniff");

  if (range) {
    const length = range.length ?? metadata.size - range.offset;
    headers.set(
      "content-range",
      `bytes ${range.offset}-${range.offset + length - 1}/${metadata.size}`,
    );
    headers.set("content-length", String(length));
  }
  if (key.endsWith(".exe") || key.endsWith(".nupkg")) {
    headers.set("content-disposition", `attachment; filename="${safeFilename(key)}"`);
  }

  return new Response(request.method === "HEAD" ? null : object.body, {
    status: range ? 206 : 200,
    headers,
  });
}

function updateContentType(key) {
  if (key.endsWith(".json")) return "application/json; charset=utf-8";
  if (key.endsWith(".mp4")) return "video/mp4";
  if (key.endsWith(".exe")) return "application/vnd.microsoft.portable-executable";
  return "application/octet-stream";
}

function updateCacheControl(key) {
  // The signed envelope is the mutable publication pointer. Packages and
  // versioned installers are immutable and may be cached indefinitely.
  return key.endsWith("feed-envelope.json") || key.includes("/releases.")
    ? "no-store"
    : "public, max-age=31536000, immutable";
}

function getDownloadKind(url) {
  const routeKind = DOWNLOAD_ROUTES.get(url.pathname.toLowerCase());
  if (routeKind) {
    return routeKind;
  }

  const queryKind = url.searchParams.get("file")?.toLowerCase();
  return queryKind === "installer" || queryKind === "idvm" ? queryKind : null;
}

function latestBundleResponse(request) {
  const headers = new Headers({
    "content-type": "text/html; charset=utf-8",
    "cache-control": "no-store",
    "content-security-policy": "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'",
    "referrer-policy": "no-referrer",
    "x-content-type-options": "nosniff",
  });

  if (request.method === "HEAD") {
    return new Response(null, { status: 200, headers });
  }

  return new Response(
    `<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Identity Vision Bridge 下载</title>
  <style>
    :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
    body { max-width: 38rem; margin: 4rem auto; padding: 0 1.25rem; line-height: 1.6; }
    button, a { font: inherit; }
    button { cursor: pointer; padding: .65rem 1rem; }
    a { margin-right: 1rem; }
  </style>
</head>
<body>
  <h1>Identity Vision Bridge</h1>
  <button id="download-all" type="button">下载全部文件</button>
  <p>
    <a href="/">单独下载 IDVB-Setup</a>
    <a href="/idvm">单独下载 IDVM</a>
  </p>
  <script>
    const files = ["/", "/idvm"];
    const download = (path) => {
      const link = document.createElement("a");
      link.href = path;
      link.download = "";
      link.rel = "noopener";
      document.body.appendChild(link);
      link.click();
      link.remove();
    };
    const downloadAll = () => files.forEach((path, index) => {
      window.setTimeout(() => download(path), index * 900);
    });
    document.getElementById("download-all").addEventListener("click", downloadAll);
  </script>
</body>
</html>`,
    { status: 200, headers },
  );
}

async function listObjects(bucket) {
  const objects = [];
  let cursor;

  do {
    const page = await bucket.list({
      limit: 1000,
      ...(cursor ? { cursor } : {}),
    });
    objects.push(...page.objects);
    cursor = page.truncated ? page.cursor : undefined;
  } while (cursor);

  return objects;
}

function selectLatest(objects, kind) {
  const matches = objects.filter((object) => {
    const key = object.key.toLowerCase();
    if (kind === "installer") {
      // The public download must never promote a test-channel installer just
      // because it was uploaded more recently than stable.
      const isLegacyRootInstaller = !key.includes("/");
      const isStableUpdateInstaller = key.startsWith("updates/win-x64-stable/");
      return (isLegacyRootInstaller || isStableUpdateInstaller)
        && key.includes("idvb-setup")
        && key.endsWith(".exe");
    }
    return key.endsWith(".idvm");
  });

  matches.sort((left, right) => {
    const uploadedDifference = new Date(right.uploaded) - new Date(left.uploaded);
    return uploadedDifference || right.key.localeCompare(left.key);
  });

  return matches[0];
}

function parseRange(value, size) {
  if (!value) {
    return null;
  }

  const match = /^bytes=(\d*)-(\d*)$/.exec(value.trim());
  if (!match || (!match[1] && !match[2])) {
    return "unsatisfiable";
  }

  let offset;
  let length;

  if (match[1]) {
    offset = Number(match[1]);
    if (!Number.isSafeInteger(offset) || offset >= size) {
      return "unsatisfiable";
    }

    const end = match[2] ? Number(match[2]) : size - 1;
    if (!Number.isSafeInteger(end) || end < offset) {
      return "unsatisfiable";
    }
    length = Math.min(end, size - 1) - offset + 1;
  } else {
    const suffixLength = Number(match[2]);
    if (!Number.isSafeInteger(suffixLength) || suffixLength <= 0) {
      return "unsatisfiable";
    }
    length = Math.min(suffixLength, size);
    offset = size - length;
  }

  return { offset, length };
}

function safeFilename(key) {
  const filename = key.split("/").pop() || "download.bin";
  return filename.replace(/[\\"\r\n]/g, "_");
}
