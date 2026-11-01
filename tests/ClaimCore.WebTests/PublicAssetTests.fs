module ClaimCore.WebTests.PublicAssetTests

open System
open System.IO
open System.Net
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Web
open ClaimCore.TestSupport.TestServerFixture

let private serve () =
    task {
        let bytes = Encoding.UTF8.GetBytes "export const publicValue = 1;"
        let relative = "assets/public-12345678.js"
        use host = new Host(true, buildAssets = [ relative, bytes ])
        use! response = host.Client.GetAsync("/" + relative)
        Expect.equal response.StatusCode HttpStatusCode.OK "Actual inventoried bytes are served"
        let! content = response.Content.ReadAsByteArrayAsync()
        Expect.equal content bytes "Response retains the verified content identity"
        Expect.isTrue (response.Headers.CacheControl |> nonNull).Public "Public build cache policy"

        Expect.isTrue
            ((response.Headers.CacheControl |> nonNull).ToString().Contains "immutable")
            "Hashed build assets are immutable"

        Expect.isFalse
            (response.Headers.Contains "Set-Cookie")
            "Public bytes do not establish a private session"

        let tag = (response.Headers.ETag |> nonNull).ToString()
        // Replacing a file cannot change the bytes behind this process's cached identity.
        File.WriteAllText(Path.Combine(host.AssetsDirectory, relative), "different bytes")
        use! unchanged = host.Client.GetAsync("/" + relative)

        if unchanged.StatusCode <> HttpStatusCode.OK then
            failtestf "Repeated public request failed: %s" host.Log

        let! actual = unchanged.Content.ReadAsByteArrayAsync()
        Expect.equal actual bytes "The verified process snapshot is the content actually served"

        use request =
            new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "/" + relative)

        request.Headers.TryAddWithoutValidation("If-None-Match", tag) |> ignore
        use! conditional = host.Client.SendAsync request

        Expect.equal
            conditional.StatusCode
            HttpStatusCode.NotModified
            "Conditional cache identity agrees"

        for path in [ "/assets/missing-12345678.js"; "/cases/synthetic"; "/api/missing" ] do
            use! protectedResponse = host.Client.GetAsync path

            Expect.isTrue
                (protectedResponse.Headers.CacheControl |> nonNull).NoStore
                "Errors and HTML fallback cannot inherit asset caching"
    }

let private independentAdmission () =
    task {
        let bytes = Encoding.UTF8.GetBytes "export const publicValue = 2;"
        use host = new Host(true, buildAssets = [ "assets/public-23456789.js", bytes ])
        let! login, _ = host.Login()
        Expect.equal login 200 "A cookie session is established before pressure"
        let admission = host.Services.GetRequiredService<AuthenticationAdmission>()
        use first = admission.Acquire(DefaultHttpContext())
        use second = admission.Acquire(DefaultHttpContext())
        Expect.isTrue (first.IsAcquired && second.IsAcquired) "The cookie budget is fully held"
        let! busy, _ = host.Bootstrap()
        Expect.equal busy 429 "Private session work still respects authentication admission"
        use! asset = host.Client.GetAsync "/assets/public-23456789.js"

        Expect.equal
            asset.StatusCode
            HttpStatusCode.OK
            "Verified public bytes need no cookie validation"

        let! actual = asset.Content.ReadAsByteArrayAsync()
        Expect.equal actual bytes "The exemption serves only the inventoried public content"

        Expect.isTrue
            (asset.Headers.CacheControl |> nonNull).Public
            "Public cache identity survives pressure"

        use! missing = host.Client.GetAsync "/assets/missing-23456789.js"

        Expect.equal
            (int missing.StatusCode)
            429
            "An asset-like unknown path cannot bypass admission"

        Expect.isTrue
            (missing.Headers.CacheControl |> nonNull).NoStore
            "Rejected unknown paths remain private"
    }

let private mismatch () =
    let directory = Directory.CreateTempSubdirectory("claimcore-public-inventory-")

    try
        Directory.CreateDirectory(Path.Combine(directory.FullName, "assets")) |> ignore
        File.WriteAllText(Path.Combine(directory.FullName, "assets/public-12345678.js"), "actual")

        File.WriteAllBytes(
            Path.Combine(directory.FullName, "public-assets.json"),
            JsonSerializer.SerializeToUtf8Bytes
                {|
                    schema = 1
                    assets =
                        [|
                            {|
                                path = "/assets/public-12345678.js"
                                sha256 = String.replicate 64 "0"
                            |}
                        |]
                |}
        )

        let mutable refused = false

        try
            PublicAssets.Inventory directory.FullName |> ignore
        with WebStartupException WebStartupProblem.AssetsMissing ->
            refused <- true

        Expect.isTrue refused "An unreviewed replacement cannot borrow a public content identity"
    finally
        directory.Delete true

[<Tests>]
let tests =
    testList
        "public asset identity"
        [
            testCaseAsync
                "caches only actual served build bytes and preserves private fallback policy"
                (async { do! serve () |> Async.AwaitTask })
            testCaseAsync
                "serves verified public bytes independently of saturated authentication"
                (async { do! independentAdmission () |> Async.AwaitTask })
            testCase "refuses an inventory that disagrees with the published content" mismatch
        ]
