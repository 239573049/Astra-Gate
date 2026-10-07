/**
 * Strict CSP for pages served from app://astra. The web app is a static
 * Vite build, so scripts/styles/images all come from 'self'; API calls go to
 * the local server origin (http://127.0.0.1:<port>).
 */
export function buildCsp(apiOrigin: string): string {
  return [
    "default-src 'self'",
    "script-src 'self'",
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob:",
    "font-src 'self' data:",
    "connect-src 'self' " + apiOrigin,
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'none'",
    "frame-ancestors 'none'",
  ].join('; ');
}
