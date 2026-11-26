module ClaimCore.ServerTests.RuntimeCatalogTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Server
open ClaimCore.TestSupport.TestServerFixture

let private catalogPath (host: Host) (language: string) =
    use manifest =
        JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(host.AssetsDirectory, "catalog-manifest.json"))
        )

    manifest.RootElement.GetProperty(language).GetString() |> nonNull

let private publicCatalog () =
    task {
        use host = new Host(true)
        let path = catalogPath host "en"
        use! first = host.SendResponse(HttpMethod.Get, path, None, [])
        Expect.equal first.StatusCode HttpStatusCode.OK "Public words need no credentials"

        Expect.isTrue
            (first.Headers.CacheControl |> nonNull).Public
            "Content-addressed catalogs are public"

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
                path,
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
            (unchanged.Headers.CacheControl |> nonNull).Public
            "304 retains the immutable asset policy"

        let! empty = unchanged.Content.ReadAsByteArrayAsync()
        Expect.isEmpty empty "304 sends no body"
        use! unknown = host.Client.GetAsync "/assets/catalogs/old-build.json"

        Expect.equal
            unknown.StatusCode
            HttpStatusCode.NotFound
            "Unknown languages do not become the SPA"

        Expect.isFalse (unknown.Headers.Contains "Set-Cookie") "Unknown languages set no cookies"
    }

let private incomplete () =
    use host = new Host(true)
    let path = catalogPath host "lv"
    File.Delete(Path.Combine(host.AssetsDirectory, path.TrimStart('/')))

    let refused =
        try
            PublicAssets.Inventory host.AssetsDirectory |> ignore
            false
        with WebStartupException WebStartupProblem.AssetsMissing ->
            true

    Expect.isTrue refused "An inventoried catalog missing from the package refuses startup."

[<Tests>]
let tests =
    testList
        "build-bound public catalogs"
        [
            testCase "GET is public conditional cookie-free and exact-language only" (fun () ->
                publicCatalog().GetAwaiter().GetResult())
            testCase "a missing published catalog refuses startup" incomplete
        ]
