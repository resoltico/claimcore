module ClaimCore.WebTests.RuntimeCatalogTests

open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.TestSupport.TestServerFixture

let private publicCatalog () =
    task {
        use host = new Host(true)
        use! first = host.SendResponse(HttpMethod.Get, "/catalogs/en.json", None, [])
        Expect.equal first.StatusCode HttpStatusCode.OK "Public words need no credentials"
        Expect.isTrue (first.Headers.CacheControl |> nonNull).NoCache "Catalogs revalidate"
        Expect.isFalse (first.Headers.Contains "Set-Cookie") "No cookie is established"
        let etag = first.Headers.ETag |> nonNull
        Expect.isFalse etag.IsWeak "The ETag identifies the exact resolved bytes"
        let! bytes = first.Content.ReadAsByteArrayAsync()
        use body = JsonDocument.Parse bytes

        Expect.equal
            (body.RootElement.GetProperty("core").GetProperty("task.case.open.label").GetString())
            "Open a case"
            "Core text is served"

        Expect.equal
            (body.RootElement.GetProperty("chrome").GetProperty("ui.loading").GetString())
            "Loading…"
            "Chrome text comes from the verified inventory"

        let! _, _ = host.Login()

        use! unchanged =
            host.SendResponse(
                HttpMethod.Get,
                "/catalogs/en.json",
                None,
                [ "If-None-Match", "W/" + etag.ToString() ]
            )

        Expect.equal
            unchanged.StatusCode
            HttpStatusCode.NotModified
            "Matching conditional GET reuses bytes"

        Expect.isFalse
            (unchanged.Headers.Contains "Set-Cookie")
            "Catalog reads do not renew an authenticated session"

        Expect.isTrue
            (unchanged.Headers.CacheControl |> nonNull).NoCache
            "304 retains revalidation policy"

        let! empty = unchanged.Content.ReadAsByteArrayAsync()
        Expect.isEmpty empty "304 sends no body"
        use! unknown = host.Client.GetAsync "/catalogs/unknown.json"

        Expect.equal
            unknown.StatusCode
            HttpStatusCode.NotFound
            "Unknown languages do not become the SPA"

        Expect.isFalse (unknown.Headers.Contains "Set-Cookie") "Unknown languages set no cookies"
    }

let private stale () =
    task {
        let translated =
            Encoding.UTF8.GetBytes
                "{\"ui.loading\":{\"text\":\"STALE_CANARY\",\"source\":\"000000000000\"}}"

        use host = new Host(true, buildAssets = [ "locales/lv.json", translated ])
        use! response = host.Client.GetAsync "/catalogs/lv.json"

        Expect.equal
            response.StatusCode
            HttpStatusCode.OK
            "A stale entry falls back without regeneration"

        let! bytes = response.Content.ReadAsByteArrayAsync()
        use body = JsonDocument.Parse bytes

        Expect.equal
            (body.RootElement.GetProperty("chrome").GetProperty("ui.loading").GetString())
            "Loading…"
            "Stale Latvian cannot accompany changed English"

        Expect.isFalse
            ((Encoding.UTF8.GetString bytes).Contains "STALE_CANARY")
            "The stale payload never reaches the browser"
    }

[<Tests>]
let tests =
    testList
        "runtime public catalogs"
        [
            testCase "GET is public conditional cookie-free and exact-language only" (fun () ->
                publicCatalog().GetAwaiter().GetResult())
            testCase "stale Latvian chrome falls back at HTTP runtime" (fun () ->
                stale().GetAwaiter().GetResult())
        ]
