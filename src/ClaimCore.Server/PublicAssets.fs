namespace ClaimCore.Server

open System
open Acornima
open Acornima.Ast
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open ClaimCore.Wire
open ClaimCore.Presentation

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
                "/catalog-manifest.json"
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

    let private literalSpecifier (source: Expression) =
        match source with
        | :? StringLiteral as literal -> Some literal.Value
        | :? TemplateLiteral as literal when literal.Expressions.Count = 0 ->
            Option.ofObj literal.Quasis[0].Value.Cooked
        | _ -> None

    // Parse the executable bytes independently of the bundler inventory. The workspace
    // ships literal dynamic imports. Its viewer is a self-contained inline script.
    let private moduleReferences (entry: Node) requireLiteral onReference onLiteral =
        let rec walk (node: Node) =
            match node with
            | :? ImportDeclaration as declaration -> onReference declaration.Source.Value
            | :? ExportNamedDeclaration as declaration ->
                declaration.Source
                |> Option.ofObj
                |> Option.iter (fun source -> onReference source.Value)
            | :? ExportAllDeclaration as declaration -> onReference declaration.Source.Value
            | :? ImportExpression as expression ->
                match literalSpecifier expression.Source with
                | Some specifier -> onReference specifier
                | None when requireLiteral ->
                    invalidOp "A browser module uses an undeclared computed import."
                | None -> ()
            | :? NewExpression as expression when expression.Arguments.Count > 0 ->
                match expression.Callee, literalSpecifier expression.Arguments[0] with
                | (:? Identifier as constructor), Some specifier when
                    constructor.Name = "Worker" || constructor.Name = "SharedWorker"
                    ->
                    onReference specifier
                | _ -> ()
            | :? StringLiteral as literal -> onLiteral literal.Value
            | :? TemplateLiteral as literal when literal.Expressions.Count = 0 ->
                literal.Quasis[0].Value.Cooked |> Option.ofObj |> Option.iter onLiteral
            | _ -> ()

            for child in node.ChildNodes do
                walk child

        walk entry

    let private validateReferences
        (inventory: JsonElement)
        (files: Map<string, Asset>)
        (viewer: Viewer)
        =
        let text path =
            UTF8Encoding(false, true).GetString(files[path].Bytes)

        let stringValue (value: JsonElement) = value.GetString() |> nonNull

        let require path =
            if not (Map.containsKey path files) then
                invalidOp $"A packaged asset refers to an unlisted dependency ({path})."

        let graph =
            inventory.GetProperty("graph").EnumerateObject()
            |> Seq.map (fun entry ->
                require entry.Name
                let targets = entry.Value.EnumerateArray() |> Seq.map stringValue |> Seq.toList
                targets |> List.iter require
                entry.Name, targets)
            |> Seq.toList

        let graphMap = Map.ofList graph

        if graph.Length <> graphMap.Count then
            invalidOp "Duplicate packaged dependency graph node."

        let identities =
            inventory.GetProperty("identityChunks").EnumerateArray()
            |> Seq.map stringValue
            |> Set.ofSeq

        for path in identities do
            require path

            if not ((text path).Contains(ContractIdentity.value, StringComparison.Ordinal)) then
                invalidOp "The compiled browser handshake identity disagrees with this server."

        let htmlReferences path =
            let document = text path

            let scripts =
                Regex.Matches(
                    document,
                    """<script\b[^>]*\bsrc\s*=\s*["']([^"']+)["']""",
                    RegexOptions.IgnoreCase
                )

            let links =
                Regex.Matches(
                    document,
                    """<link\b[^>]*\bhref\s*=\s*["']([^"']+)["']""",
                    RegexOptions.IgnoreCase
                )

            let references (matches: MatchCollection) =
                matches |> Seq.map (fun m -> m.Groups[1].Value)

            let roots = references scripts |> Seq.toList
            Seq.append roots (references links) |> Seq.iter require
            roots

        let rec reachable seen pending =
            match pending with
            | [] -> seen
            | path :: rest when Set.contains path seen -> reachable seen rest
            | path :: rest ->
                reachable
                    (Set.add path seen)
                    ((Map.tryFind path graphMap |> Option.defaultValue []) @ rest)

        let workspace = htmlReferences indexPath
        let recovery = htmlReferences recoveryPath

        if List.isEmpty workspace || List.isEmpty recovery then
            invalidOp "A packaged browser document is missing its script roots."

        for script in workspace @ recovery do
            if not (Map.containsKey script graphMap) then
                invalidOp "A packaged HTML script is absent from the dependency graph."

        if (Set.intersect (reachable Set.empty workspace) identities).IsEmpty then
            invalidOp "The workspace cannot reach the compiled handshake identity."

        let resolve path (target: string) =
            let origin = Uri("https://public.invalid" + path)
            let uri = Uri(origin, target)

            if
                uri.Host <> origin.Host
                || uri.Scheme <> origin.Scheme
                || uri.Port <> origin.Port
            then
                invalidOp "A packaged dependency refers outside the public inventory."

            require uri.AbsolutePath
            uri.AbsolutePath

        // Vite's preload table and compiled resource mappings carry root asset
        // paths as string literals. They need admission even when no import names them.
        let assetLiteral (value: string) =
            if
                Regex.IsMatch(
                    value,
                    """^/?assets/[A-Za-z0-9_./~-]+\.(?:js|css|json|svg|png|webp|woff2|wasm|bcmap)\z"""
                )
            then
                require ("/" + value.TrimStart('/'))

        let parser = Parser()

        for KeyValue(path, dependencies) in graphMap do
            if not (path.EndsWith(".js", StringComparison.Ordinal)) then
                invalidOp "A bundle graph node must be a JavaScript module."

            let reference (specifier: string) =
                if
                    not (
                        specifier.StartsWith("./", StringComparison.Ordinal)
                        || specifier.StartsWith("../", StringComparison.Ordinal)
                        || specifier.StartsWith("/", StringComparison.Ordinal)
                    )
                then
                    invalidOp "A browser module dependency must be a packaged URL."

                let target = resolve path specifier

                if not (List.contains target dependencies && Map.containsKey target graphMap) then
                    invalidOp "A compiled module dependency is absent from the packaged graph."

            moduleReferences (parser.ParseModule(text path)) true reference assetLiteral

        let inlineScript =
            Regex
                .Match(
                    UTF8Encoding(false, true).GetString viewer.Bytes,
                    """<script>([\s\S]*?)</script>"""
                )
                .Groups[1].Value

        let workerPrograms = ResizeArray<string>()

        let inlineDependency _ =
            invalidOp "The inline attachment viewer cannot load a packaged module."

        moduleReferences (parser.ParseScript inlineScript) false inlineDependency (fun literal ->
            assetLiteral literal

            if Regex.IsMatch(literal, @"^var\s+ClaimCorePdfWorker\s*=") then
                workerPrograms.Add literal)

        for KeyValue(path, asset) in files do
            if asset.ContentType.StartsWith("text/css", StringComparison.Ordinal) then
                for entry in
                    Regex.Matches(
                        text path,
                        """url\(\s*["']?([^"'\s)]+)""",
                        RegexOptions.IgnoreCase
                    ) do
                    let target = entry.Groups[1].Value

                    if
                        not (
                            target.StartsWith("data:", StringComparison.Ordinal)
                            || target.StartsWith("#", StringComparison.Ordinal)
                        )
                    then
                        resolve path target |> ignore

        let viewerResources =
            inventory.GetProperty("viewer").GetProperty("resources").EnumerateArray()
            |> Seq.map stringValue
            |> Seq.toList

        viewerResources |> List.iter require

        let resourceReferences =
            graphMap
            |> Map.keys
            |> Seq.collect (fun path ->
                Regex.Matches(text path, """/assets/pdf-[a-zA-Z0-9_.-]+\.(?:bcmap|wasm)""")
                |> Seq.map _.Value)
            |> Set.ofSeq

        if not resourceReferences.IsEmpty && workerPrograms.Count <> 1 then
            invalidOp "The published viewer must contain its compiled worker."

        for worker in workerPrograms do
            moduleReferences (parser.ParseScript worker) false inlineDependency assetLiteral

        if
            resourceReferences <> Set.ofList viewerResources
            || resourceReferences.Count <> viewerResources.Length
        then
            invalidOp "Packaged viewer resources disagree with the compiled viewer mapping."

    let private load directory =
        let root = Path.GetFullPath directory + string Path.DirectorySeparatorChar
        let manifest = Path.Combine(directory, "public-assets.json")
        use document = JsonDocument.Parse(File.ReadAllBytes manifest)

        if document.RootElement.GetProperty("schema").GetInt32() <> 5 then
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

        use catalogManifest = JsonDocument.Parse(files["/catalog-manifest.json"].Bytes)

        let catalogPaths =
            catalogManifest.RootElement.EnumerateObject()
            |> Seq.map (fun entry ->
                let path = entry.Value.GetString() |> nonNull
                let bytes = files[path].Bytes
                let expected = $"/assets/catalogs/{entry.Name}.{digest bytes}.json"

                if path <> expected then
                    invalidOp "Public catalog content identity differs."

                entry.Name, path)
            |> Seq.toList

        if
            (catalogPaths |> List.map fst |> List.sort)
            <> List.sort LocalizationPolicy.languages
        then
            invalidOp "The public catalog inventory must cover every offered language exactly once."

        validateReferences document.RootElement files viewer
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
                    |> Seq.collect (fun value -> (nonNull value).Split(','))
                    |> Seq.map _.Trim()
                    |> Seq.exists (fun value ->
                        value = asset.ETag || value = "W/" + asset.ETag || value = "*")

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
