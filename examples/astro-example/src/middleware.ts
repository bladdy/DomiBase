import { defineMiddleware } from "astro:middleware";

/**
 * Defence in depth for a server-rendered page that shows project data.
 * The API keys never leave the server; these headers keep the HTML from being
 * framed, sniffed, cached or embedded elsewhere.
 */
export const onRequest = defineMiddleware(async (_context, next) => {
  const response = await next();

  response.headers.set("X-Content-Type-Options", "nosniff");
  response.headers.set("X-Frame-Options", "DENY");
  response.headers.set("Referrer-Policy", "no-referrer");
  response.headers.set("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
  response.headers.set("Cross-Origin-Opener-Policy", "same-origin");
  response.headers.set("Cache-Control", "no-store");

  // Vite injects development tooling that is incompatible with a strict CSP.
  if (!import.meta.env.DEV) {
    response.headers.set(
      "Content-Security-Policy",
      [
        "default-src 'self'",
        "base-uri 'self'",
        "connect-src 'self'",
        "form-action 'self'",
        "frame-ancestors 'none'",
        "img-src 'self' data:",
        "object-src 'none'",
        "script-src 'self'",
        "style-src 'self' 'unsafe-inline'"
      ].join("; ")
    );
  }

  return response;
});
