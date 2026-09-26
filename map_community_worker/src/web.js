/**
 * Optional integration into the existing community Worker. Copy dist/ into
 * its public/web/ directory and call this before its existing API/assets router:
 *   const web = await serveIdvbWeb(request, env);
 *   if (web) return web;
 * This is static hosting only; screenshots and crypto never enter the Worker.
 */
export async function serveIdvbWeb(request, env) {
  const url = new URL(request.url);
  if (url.pathname === '/web') return Response.redirect(new URL('/web/', url).toString(), 308);
  if (!url.pathname.startsWith('/web/')) return undefined;
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    return new Response('Method Not Allowed', { status: 405, headers: { allow: 'GET, HEAD' } });
  }
  const asset = await env.ASSETS.fetch(request);
  const headers = new Headers(asset.headers);
  // Replace the community landing page's policy (which disallows Blob maps),
  // do not append a second restrictive policy. API authentication stays unchanged.
  headers.set(
    'Content-Security-Policy',
    "default-src 'self'; script-src 'self'; worker-src 'self'; style-src 'self'; connect-src 'self' https://download.xgflee.com; img-src 'self' blob: data:; media-src 'self' blob:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'",
  );
  headers.set('Referrer-Policy', 'no-referrer');
  headers.set('X-Content-Type-Options', 'nosniff');
  headers.set('Permissions-Policy', 'camera=(), microphone=(), geolocation=()');
  if (url.pathname.endsWith('/') || url.pathname.endsWith('.html')) {
    // Cloudflare honours no-transform and does not inject a Web Analytics beacon.
    // no-cache still requires revalidation of the app shell on every visit.
    headers.set('Cache-Control', 'public, no-cache, no-transform');
  } else if (url.pathname.endsWith('/sw.js') || url.pathname.endsWith('.webmanifest')) {
    headers.set('Cache-Control', 'no-cache');
  }
  return new Response(asset.body, { status: asset.status, statusText: asset.statusText, headers });
}
