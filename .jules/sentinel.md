## 2024-05-23 - [Security Enhancements]
**Vulnerability:** Missing Security Headers and Information Disclosure
**Learning:** The default ASP.NET Core template does not include some important security headers by default, and exposes the server header "Kestrel".
**Prevention:** Explicitly added middleware to inject `X-Content-Type-Options`, `Referrer-Policy`, and `Permissions-Policy`. Disabled `AddServerHeader` in Kestrel options. Added `UseHsts()` for production.
