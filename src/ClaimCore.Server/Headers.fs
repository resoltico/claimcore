namespace ClaimCore.Server

open System
open System.Threading.Tasks
open ClaimCore.Wire
open Microsoft.AspNetCore.Http

module HttpHeaders =
    let private publicAssetKey = obj ()
    let private catalogKey = obj ()
    let private binaryKey = obj ()
    let private viewerKey = obj ()
    let internal noTransform (context: HttpContext) = context.Items[binaryKey] <- box true
    let internal catalog (context: HttpContext) = context.Items[catalogKey] <- box true

    let internal publicAsset (context: HttpContext) =
        context.Items[publicAssetKey] <- box true

    let internal viewer scriptHash (context: HttpContext) =
        context.Items[viewerKey] <- box scriptHash

    let private contentSecurityPolicy frameSource =
        "default-src 'none'; script-src 'self'; style-src 'self'; style-src-attr 'none'; "
        + "img-src 'self' blob:; font-src 'self'; connect-src 'self'; object-src 'none'; "
        + "base-uri 'none'; frame-ancestors 'none'; frame-src "
        + frameSource
        + "; form-action 'self'; "
        + "manifest-src 'self'; media-src 'none'; worker-src 'none'; "
        + "require-trusted-types-for 'script'; trusted-types 'none'"

    let private viewerPolicy scriptHash =
        $"sandbox allow-scripts; default-src 'none'; script-src 'sha256-{scriptHash}' 'wasm-unsafe-eval'; "
        + "style-src 'none'; img-src blob: data:; connect-src 'none'; "
        + "worker-src blob:; frame-ancestors 'self'; base-uri 'none'; form-action 'none'"

    let private permissionsPolicy =
        [
            "accelerometer=()"
            "autoplay=()"
            "bluetooth=()"
            "browsing-topics=()"
            "camera=()"
            "clipboard-read=()"
            "display-capture=()"
            "encrypted-media=()"
            "geolocation=()"
            "gyroscope=()"
            "hid=()"
            "idle-detection=()"
            "local-fonts=()"
            "magnetometer=()"
            "microphone=()"
            "midi=()"
            "payment=()"
            "publickey-credentials-create=()"
            "publickey-credentials-get=()"
            "screen-wake-lock=()"
            "serial=()"
            "usb=()"
            "xr-spatial-tracking=()"
        ]
        |> String.concat ", "

    let internal apply (origin: Uri) (context: HttpContext) =
        let headers = context.Response.Headers

        match context.Items.TryGetValue viewerKey with
        | true, (:? string as scriptHash) ->
            headers.ContentSecurityPolicy <- viewerPolicy scriptHash
            headers.Remove "X-Frame-Options" |> ignore
        | _ ->
            let frameSource =
                if
                    (context.Response.ContentType |> Option.ofObj)
                    |> Option.exists (fun value ->
                        value.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                then
                    Uri(origin, AttachmentTransport.viewerPath).AbsoluteUri
                else
                    "'none'"

            headers.ContentSecurityPolicy <- contentSecurityPolicy frameSource
            headers.["X-Frame-Options"] <- "DENY"

        headers.XContentTypeOptions <- "nosniff"
        headers.["Referrer-Policy"] <- "no-referrer"
        headers.["Strict-Transport-Security"] <- "max-age=31536000"
        headers.["Permissions-Policy"] <- permissionsPolicy
        headers.["Cross-Origin-Opener-Policy"] <- "same-origin"
        headers.["Cross-Origin-Resource-Policy"] <- "same-origin"
        headers.["X-Robots-Tag"] <- "noindex, nofollow"

    let noStore (context: HttpContext) =
        let headers = context.Response.Headers
        headers.CacheControl <- "no-store, max-age=0"
        headers.Pragma <- "no-cache"
        headers.Expires <- "0"

    /// Registered outermost: reverse callback order makes this the final owned policy.
    let register origin (context: HttpContext) =
        context.Response.OnStarting(
            Func<Task>(fun () ->
                apply origin context

                let publicResponse =
                    context.Items.ContainsKey publicAssetKey
                    && (context.Response.StatusCode = 200 || context.Response.StatusCode = 304)
                    && not (context.Response.Headers.ContainsKey "Set-Cookie")

                if publicResponse then
                    context.Response.Headers.CacheControl <- "public, max-age=31536000, immutable"
                    context.Response.Headers.Remove "Pragma" |> ignore
                    context.Response.Headers.Remove "Expires" |> ignore
                elif
                    context.Items.ContainsKey catalogKey
                    && not (context.Response.Headers.ContainsKey "Set-Cookie")
                then
                    context.Response.Headers.CacheControl <- "no-cache"
                    context.Response.Headers.Remove "Pragma" |> ignore
                    context.Response.Headers.Remove "Expires" |> ignore
                else
                    noStore context

                    if context.Items.ContainsKey binaryKey then
                        context.Response.Headers.CacheControl <-
                            "no-store, no-transform, max-age=0"

                Task.CompletedTask)
        )
