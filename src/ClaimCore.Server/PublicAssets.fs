namespace ClaimCore.Server

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

/// A process serves the exact verified public bytes captured from its published build inventory.
module internal PublicAssets =
    type Viewer =
        {
            Bytes: byte array
            ScriptHash: string
            StyleHash: string
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
            StyleHash = hash "style" "styleSha256"
        }

    type private Asset =
        {
            Bytes: byte array
            ContentType: string
            ETag: string
        }

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
        | _ -> invalidOp "Unsupported public build asset."

    let private load directory =
        let root = Path.GetFullPath directory + string Path.DirectorySeparatorChar
        let manifest = Path.Combine(directory, "public-assets.json")
        use document = JsonDocument.Parse(File.ReadAllBytes manifest)

        if document.RootElement.GetProperty("schema").GetInt32() <> 2 then
            invalidOp "Unsupported public inventory."

        let viewer = readViewer directory document.RootElement

        let assets =
            document.RootElement.GetProperty("assets").EnumerateArray()
            |> Seq.map (fun item ->
                let path = item.GetProperty("path").GetString() |> nonNull
                let expected = item.GetProperty("sha256").GetString() |> nonNull
                let file = Path.GetFullPath(Path.Combine(directory, path.TrimStart('/')))

                if
                    not (
                        path.StartsWith("/assets/", StringComparison.Ordinal)
                        || path = "/react-aria-pressable.css"
                        || path.StartsWith("/locales/", StringComparison.Ordinal)
                    )
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

        Map.ofList assets, viewer

    type Inventory(directory: string) =
        let assets, viewer =
            try
                load directory
            with _ ->
                WebStartupDiagnostics.refuse WebStartupProblem.AssetsMissing

        member _.TryRead path =
            assets |> Map.tryFind path |> Option.map _.Bytes

        member _.Viewer = viewer

        member _.Serve(context: HttpContext, next: RequestDelegate) : Task =
            task {
                match
                    assets |> Map.tryFind (defaultArg (Option.ofObj context.Request.Path.Value) "")
                with
                | None -> do! next.Invoke context
                | Some asset when
                    HttpMethods.IsGet context.Request.Method
                    || HttpMethods.IsHead context.Request.Method
                    ->
                    context.Response.ContentType <- asset.ContentType
                    context.Response.Headers.ETag <- asset.ETag
                    // Fixed-name styles must not inherit immutable caching across builds.
                    if context.Request.Path.StartsWithSegments(PathString "/assets") then
                        HttpHeaders.publicAsset context

                    let matched =
                        context.Request.Headers.IfNoneMatch
                        |> Seq.exists (fun value -> value = asset.ETag || value = "*")

                    if matched then
                        context.Response.StatusCode <- StatusCodes.Status304NotModified
                    else
                        context.Response.ContentLength <- Nullable(int64 asset.Bytes.Length)

                        if HttpMethods.IsGet context.Request.Method then
                            do! context.Response.Body.WriteAsync(asset.Bytes).AsTask()
                | Some _ ->
                    context.Response.StatusCode <- StatusCodes.Status405MethodNotAllowed
                    HttpHeaders.noStore context
            }
