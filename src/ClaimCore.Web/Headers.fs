namespace ClaimCore.Web

open Microsoft.AspNetCore.Http

module HttpHeaders =
    let private contentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; style-src-attr 'none'; "
        + "img-src 'self'; font-src 'self'; connect-src 'self'; object-src 'none'; "
        + "base-uri 'none'; frame-ancestors 'none'; frame-src 'none'; form-action 'self'; "
        + "manifest-src 'self'; media-src 'none'; worker-src 'none'"

    let apply (context: HttpContext) =
        let headers = context.Response.Headers
        headers.ContentSecurityPolicy <- contentSecurityPolicy
        headers.XContentTypeOptions <- "nosniff"
        headers.["Referrer-Policy"] <- "no-referrer"
        headers.["Permissions-Policy"] <- "camera=(), geolocation=(), microphone=(), payment=()"
        headers.["Cross-Origin-Opener-Policy"] <- "same-origin"
        headers.["Cross-Origin-Resource-Policy"] <- "same-origin"
        headers.["X-Frame-Options"] <- "DENY"
        headers.["X-Robots-Tag"] <- "noindex, nofollow"

    let noStore (context: HttpContext) =
        let headers = context.Response.Headers
        headers.CacheControl <- "no-store, max-age=0"
        headers.Pragma <- "no-cache"
        headers.Expires <- "0"
