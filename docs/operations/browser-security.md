# Browser response security

The application sets browser security headers after trusted forwarded headers are processed
and before exception handling, static files, authentication, API routes, and SPA fallback.
Headers are applied when the response starts so handled errors retain the same protections.

| Header | Policy |
| --- | --- |
| `Strict-Transport-Security` | `max-age=31536000` on HTTPS responses except localhost and loopback addresses |
| `X-Content-Type-Options` | `nosniff` |
| `X-Frame-Options` | `DENY` |
| `Content-Security-Policy` | `frame-ancestors 'none'` |
| `Referrer-Policy` | `no-referrer` |

HSTS uses the request scheme established by the configured proxy trust boundary; the application
does not read raw forwarded headers independently. HTTP liveness and readiness probes remain HTTP
without redirects. HSTS does not opt other subdomains into HTTPS or request browser preload.
Ingress remains responsible for redirecting public HTTP to HTTPS.

The CSP prevents framing and deliberately does not establish script, style, image, or connection
source restrictions. It permits the existing SPA bundles, styles, and authenticated photo previews
which use `blob:` object URLs. This is a framing defense, not a complete CSP defense against script
injection. A broader policy requires separate compatibility verification. Referrers are suppressed
even on same-origin requests to protect legacy recovery URLs that can contain query tokens.

`BrowserSecurityHeadersTests` exercises the real request pipeline for static/default documents,
SPA routes, API responses, authentication failures, handled errors, local/HTTP health probes, and
trusted versus untrusted forwarded TLS metadata. Successful photo decoding/rendering remains part
of the authenticated browser workflow verification.
