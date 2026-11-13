namespace ClaimCore.Server

open System
open System.IO
open System.Reflection
open System.Security.Cryptography
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ClaimCore.Presentation
open ClaimCore.Json

/// Resolved text is public, admitted with ingress, and served before any cookie authentication.
module internal CatalogRoutes =
    type Inventory(files: PublicAssets.Inventory) =
        let assembly = Assembly.GetExecutingAssembly()

        let core language =
            use stream =
                assembly.GetManifestResourceStream(
                    "ClaimCore.Localization." + language + ".core.json"
                )
                |> nonNull

            use bytes = new MemoryStream()
            stream.CopyTo bytes
            bytes.ToArray()

        let chrome language =
            files.TryRead("/locales/" + language + ".json")
            |> Option.defaultWith (fun () ->
                WebStartupDiagnostics.refuse WebStartupProblem.AssetsMissing)

        let english = [ core "en"; chrome "en" ]

        let catalogs =
            LocalizationPolicy.languages
            |> List.map (fun language ->
                let entries, _ =
                    LocalizationPolicy.load language english [ core language; chrome language ]

                entries
                |> Map.toList
                |> Messages.catalog
                |> Result.defaultWith invalidOp
                |> ignore

                let values predicate =
                    entries
                    |> Map.toList
                    |> List.filter (fst >> predicate)
                    |> List.map (fun (key, value) -> key, JsonValue.String value)
                    |> JsonValue.Object

                let isChrome (key: string) =
                    key.StartsWith("ui.", StringComparison.Ordinal)

                let bytes =
                    JsonValue.Object
                        [ "core", values (isChrome >> not); "chrome", values isChrome ]
                    |> CanonicalWriter.bytes

                "/catalogs/" + language + ".json",
                (bytes, "\"" + Convert.ToHexStringLower(SHA256.HashData bytes) + "\""))
            |> Map.ofList

        member _.Serve(context: HttpContext, next: RequestDelegate) : Task =
            task {
                if not (context.Request.Path.StartsWithSegments(PathString "/catalogs")) then
                    do! next.Invoke context
                else
                    HttpHeaders.catalog context

                    match
                        Map.tryFind
                            (defaultArg (Option.ofObj context.Request.Path.Value) "")
                            catalogs
                    with
                    | None -> context.Response.StatusCode <- StatusCodes.Status404NotFound
                    | Some _ when not (HttpMethods.IsGet context.Request.Method) ->
                        context.Response.StatusCode <- StatusCodes.Status405MethodNotAllowed
                        context.Response.Headers.Allow <- "GET"
                    | Some(bytes, etag) ->
                        context.Response.Headers.ETag <- etag

                        let matched =
                            context.Request.Headers.IfNoneMatch
                            |> Seq.collect (fun value -> (nonNull value).Split(','))
                            |> Seq.map _.Trim()
                            |> Seq.exists (fun tag -> tag = "*" || tag = etag || tag = "W/" + etag)

                        if matched then
                            context.Response.StatusCode <- StatusCodes.Status304NotModified
                        else
                            context.Response.ContentType <- "application/json; charset=utf-8"
                            context.Response.ContentLength <- Nullable(int64 bytes.Length)

                            do!
                                context.Response.Body
                                    .WriteAsync(bytes, context.RequestAborted)
                                    .AsTask()
            }
