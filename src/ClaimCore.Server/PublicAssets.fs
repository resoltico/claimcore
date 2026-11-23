namespace ClaimCore.Server

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open ClaimCore.Wire

/// A process serves the exact verified public bytes captured from its published build inventory.
module internal PublicAssets =
    type Viewer =
        {
            Bytes: byte array
            ScriptHash: string
        }

    let private digest bytes =
        Convert.ToHexStringLower(SHA256.HashData(bytes: byte array))

    let private readViewer directory (inventory: JsonElement) =
        let viewer = inventory.GetProperty "viewer"
        let bytes = File.ReadAllBytes(Path.Combine(directory, "attachment-preview.viewer"))

        if digest bytes <> (viewer.GetProperty("sha256").GetString() |> nonNull) then
            invalidOp "The published attachment viewer differs from its inventory."

        let html = UTF8Encoding(false, true).GetString bytes

        let hash tag (key: string) =
            let blocks =
                Regex.Matches(
                    html,
                    $"<{tag}>([\\s\\S]*?)</{tag}>",
                    RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
                )

            if blocks.Count <> 1 then
                invalidOp "The published attachment viewer is missing its unique inline block."

            let content = blocks[0].Groups[1].Value

            let actual =
                content |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToBase64String

            if actual <> (viewer.GetProperty(key).GetString() |> nonNull) then
                invalidOp
                    "The published attachment viewer inline content differs from its inventory."

            actual

        {
            Bytes = bytes
            ScriptHash = hash "script" "scriptSha256"
        }

    type private Asset =
        {
            Bytes: byte array
            ContentType: string
            ETag: string
        }

    let private indexPath = "/index.html"
    let private recoveryPath = "/auth-recovery.html"
    let private sourcePath = "/" + LegalNotice.sourceFile

    let private requiredPaths =
        set
            [
                indexPath
                recoveryPath
                "/" + LegalNotice.licenseFile + ".txt"
                "/" + LegalNotice.projectNotice
                "/" + LegalNotice.commercialNotice
                "/" + LegalNotice.thirdPartyNotices
                sourcePath
            ]

    let private mediaType path =
        match Path.GetExtension(path: string) with
        | ".js" -> "text/javascript; charset=utf-8"
        | ".json" -> "application/json; charset=utf-8"
        | ".css" -> "text/css; charset=utf-8"
        | ".svg" -> "image/svg+xml"
        | ".png" -> "image/png"
        | ".webp" -> "image/webp"
        | ".woff2" -> "font/woff2"
        | ".bcmap" -> "application/octet-stream"
        | ".wasm" -> "application/wasm"
        | ".html" -> "text/html; charset=utf-8"
        | ".txt" -> "text/plain; charset=utf-8"
        | ".md" -> "text/markdown; charset=utf-8"
        | ".gz" -> "application/gzip"
        | _ -> invalidOp "Unsupported public build asset."

    let private load directory =
        let root = Path.GetFullPath directory + string Path.DirectorySeparatorChar
        let manifest = Path.Combine(directory, "public-assets.json")
        use document = JsonDocument.Parse(File.ReadAllBytes manifest)

        if document.RootElement.GetProperty("schema").GetInt32() <> 3 then
            invalidOp "Unsupported public inventory."

        let viewer = readViewer directory document.RootElement

        let assets =
            document.RootElement.GetProperty("assets").EnumerateArray()
            |> Seq.map (fun item ->
                let path = item.GetProperty("path").GetString() |> nonNull
                let expected = item.GetProperty("sha256").GetString() |> nonNull
                let file = Path.GetFullPath(Path.Combine(directory, path.TrimStart('/')))

                let canonical =
                    "/"
                    + Path
                        .GetRelativePath(directory, file)
                        .Replace(Path.DirectorySeparatorChar, '/')

                if
                    not (
                        path.StartsWith("/assets/", StringComparison.Ordinal)
                        || path = "/react-aria-pressable.css"
                        || path = "/styles.css"
                        || path = "/brand.svg"
                        || path.StartsWith("/locales/", StringComparison.Ordinal)
                        || requiredPaths.Contains path
                    )
                    || path <> canonical
                    || not (file.StartsWith(root, StringComparison.Ordinal))
                then
                    invalidOp "Invalid public build path."

                let bytes = File.ReadAllBytes file
                let hash = digest bytes

                if hash <> expected then
                    invalidOp "Public build content identity differs."

                path,
                {
                    Bytes = bytes
                    ContentType = mediaType file
                    ETag = "\"" + hash + "\""
                })
            |> Seq.toList

        if assets.Length <> (assets |> List.map fst |> Set.ofList |> Set.count) then
            invalidOp "Duplicate public build path."

        let files = Map.ofList assets

        if requiredPaths |> Set.exists (fun path -> not (Map.containsKey path files)) then
            invalidOp "Required public build document missing from its inventory."

        files, viewer

    let private serveAsset (context: HttpContext) (path: string) asset : Task =
        task {
            if
                HttpMethods.IsGet context.Request.Method
                || HttpMethods.IsHead context.Request.Method
            then
                context.Response.ContentType <- asset.ContentType
                context.Response.Headers.ETag <- asset.ETag

                // Fixed names preserve their existing no-store policy.
                if path.StartsWith("/assets/", StringComparison.Ordinal) then
                    HttpHeaders.publicAsset context

                if path = sourcePath then
                    let disposition = "attachment; filename=\"" + LegalNotice.sourceFile + "\""
                    context.Response.Headers.ContentDisposition <- StringValues(disposition)

                let matched =
                    context.Request.Headers.IfNoneMatch
                    |> Seq.exists (fun value -> value = asset.ETag || value = "*")

                if matched then
                    context.Response.StatusCode <- StatusCodes.Status304NotModified
                else
                    context.Response.ContentLength <- Nullable(int64 asset.Bytes.Length)

                    if HttpMethods.IsGet context.Request.Method then
                        do!
                            context.Response.Body
                                .WriteAsync(asset.Bytes, context.RequestAborted)
                                .AsTask()
            else
                context.Response.StatusCode <- StatusCodes.Status405MethodNotAllowed
                context.Response.Headers.Allow <- "GET, HEAD"
                HttpHeaders.noStore context
        }

    type Inventory(directory: string) =
        let assets, viewer =
            try
                load directory
            with _ ->
                WebStartupDiagnostics.refuse WebStartupProblem.AssetsMissing

        member _.TryRead path =
            assets |> Map.tryFind path |> Option.map _.Bytes

        member _.Viewer = viewer

        member _.Recovery = assets[recoveryPath].Bytes

        member _.ServeIndex context =
            serveAsset context indexPath assets[indexPath]

        member _.Serve(context: HttpContext, next: RequestDelegate) =
            let path = defaultArg (Option.ofObj context.Request.Path.Value) ""

            match Map.tryFind path assets with
            | Some asset -> serveAsset context path asset
            | None -> next.Invoke context
