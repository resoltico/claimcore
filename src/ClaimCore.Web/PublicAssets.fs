namespace ClaimCore.Web

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

/// A process serves the exact verified public bytes captured from its published build inventory.
module internal PublicAssets =
    type private Asset =
        {
            Bytes: byte array
            ContentType: string
            ETag: string
        }

    let private mediaType path =
        match Path.GetExtension(path: string) with
        | ".js" -> "text/javascript; charset=utf-8"
        | ".css" -> "text/css; charset=utf-8"
        | ".svg" -> "image/svg+xml"
        | ".png" -> "image/png"
        | ".webp" -> "image/webp"
        | ".woff2" -> "font/woff2"
        | _ -> invalidOp "Unsupported public build asset."

    let private load directory =
        let root = Path.GetFullPath directory + string Path.DirectorySeparatorChar
        let manifest = Path.Combine(directory, "public-assets.json")
        use document = JsonDocument.Parse(File.ReadAllBytes manifest)

        if document.RootElement.GetProperty("schema").GetInt32() <> 1 then
            invalidOp "Unsupported public inventory."

        document.RootElement.GetProperty("assets").EnumerateArray()
        |> Seq.map (fun item ->
            let path = item.GetProperty("path").GetString() |> nonNull
            let expected = item.GetProperty("sha256").GetString() |> nonNull
            let file = Path.GetFullPath(Path.Combine(directory, path.TrimStart('/')))

            if
                not (path.StartsWith("/assets/", StringComparison.Ordinal))
                || not (file.StartsWith(root, StringComparison.Ordinal))
            then
                invalidOp "Invalid public build path."

            let bytes = File.ReadAllBytes file
            let hash = Convert.ToHexStringLower(SHA256.HashData bytes)

            if hash <> expected then
                invalidOp "Public build content identity differs."

            path,
            {
                Bytes = bytes
                ContentType = mediaType file
                ETag = "\"" + hash + "\""
            })
        |> Seq.toList
        |> fun assets ->
            if assets.Length <> (assets |> List.map fst |> Set.ofList |> Set.count) then
                invalidOp "Duplicate public build path."

            Map.ofList assets

    type Inventory(directory: string) =
        let assets =
            try
                load directory
            with _ ->
                WebStartupDiagnostics.refuse WebStartupProblem.AssetsMissing

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
