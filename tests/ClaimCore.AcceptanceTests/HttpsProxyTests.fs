module ClaimCore.AcceptanceTests.HttpsProxyTests

open System
open System.IO
open System.Net.Http
open System.Text
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging

[<Tests>]
let tests =
    testCaseAsync
        "acceptance HTTPS relay preserves declared CSV length and reframes only owned rewrites"
        (async {
            let directory = Directory.CreateTempSubdirectory "claimcore-relay-"
            use certificate = FixtureFiles.certificate ()
            let backend = "http://localhost:" + string (FixtureFiles.port ())
            let origin = "https://localhost:" + string (FixtureFiles.port ())
            let csv = Encoding.UTF8.GetBytes "reference,action\r\nSYNTHETIC,'+α reminder\r\n"
            let original = Encoding.UTF8.GetBytes "{}"
            let rewritten = Encoding.UTF8.GetBytes "{\"owned\":\"expanded α\"}"
            let builder = WebApplication.CreateBuilder()
            builder.Logging.ClearProviders() |> ignore
            builder.WebHost.UseUrls backend |> ignore
            let application = builder.Build()

            application.Run(
                RequestDelegate(fun (context: HttpContext) ->
                    task {
                        let isCsv = context.Request.Path.Value = "/csv"

                        context.Response.ContentType <-
                            if isCsv then
                                "text/csv; charset=utf-8"
                            else
                                "application/json"

                        context.Response.Headers.ContentEncoding <- "identity"

                        if isCsv then
                            context.Response.ContentLength <- Nullable(int64 csv.Length)

                            context.Response.Headers.ContentDisposition <-
                                "attachment; filename=\"claimcore-cases.csv\""

                            context.Response.Headers.CacheControl <- "no-store, no-transform"
                            do! context.Response.Body.WriteAsync csv
                        elif context.Request.Path.Value = "/without-length" then
                            do! context.Response.StartAsync()
                            do! context.Response.Body.WriteAsync original
                        else
                            context.Response.ContentLength <- Nullable(int64 original.Length)
                            do! context.Response.Body.WriteAsync original
                    })
            )

            use upstream =
                new HttpClient(
                    new HttpClientHandler(
                        AllowAutoRedirect = false,
                        UseCookies = false,
                        UseProxy = false
                    )
                )

            let proxy =
                new HttpsProxy.Proxy(origin, backend, certificate, upstream, directory.FullName)

            use handler =
                new HttpClientHandler(
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseProxy = false
                )

            handler.ServerCertificateCustomValidationCallback <-
                fun _ supplied _ _ ->
                    match supplied with
                    | null -> false
                    | value -> value.RawData = certificate.RawData

            use client = new HttpClient(handler, Timeout = TimeSpan.FromSeconds 5.)

            try
                do! application.StartAsync() |> Async.AwaitTask
                do! proxy.Start() |> Async.AwaitTask

                use! response =
                    client.GetAsync(origin + "/csv", HttpCompletionOption.ResponseHeadersRead)
                    |> Async.AwaitTask

                Expect.equal
                    response.Content.Headers.ContentLength
                    (Nullable(int64 csv.Length))
                    "The original declared length exists before any body buffering"

                Expect.equal
                    (response.Content.Headers.ContentEncoding |> Seq.toList)
                    [ "identity" ]
                    "No encoding transformation"

                Expect.equal
                    (response.Content.Headers.GetValues("Content-Disposition") |> Seq.exactlyOne)
                    "attachment; filename=\"claimcore-cases.csv\""
                    "Fixed attachment survives"

                let! delivered = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
                Expect.equal delivered csv "The relay preserves every original UTF-8 CSV byte"

                use! unknown =
                    client.GetAsync(
                        origin + "/without-length",
                        HttpCompletionOption.ResponseHeadersRead
                    )
                    |> Async.AwaitTask

                Expect.isFalse
                    unknown.Content.Headers.ContentLength.HasValue
                    "An upstream stream without a declared length stays undeclared"

                let! unknownBody = unknown.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
                Expect.equal unknownBody original "Undeclared body still relays unchanged"

                proxy.TransformNextReply(
                    "/rewrite",
                    fun bytes ->
                        Expect.equal bytes original "Only the selected upstream body is transformed"
                        rewritten
                )

                use! transformed =
                    client.GetAsync(origin + "/rewrite", HttpCompletionOption.ResponseHeadersRead)
                    |> Async.AwaitTask

                Expect.equal
                    transformed.Content.Headers.ContentLength
                    (Nullable(int64 rewritten.Length))
                    "Owned rewrite declares its exact new length rather than the upstream length"

                let! changed = transformed.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
                Expect.equal changed rewritten "The declared rewrite is the actual body"
            finally
                (proxy :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()
                application.DisposeAsync().AsTask().GetAwaiter().GetResult()
                directory.Delete true
        })
