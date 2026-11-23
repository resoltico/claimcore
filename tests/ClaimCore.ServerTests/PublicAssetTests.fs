module ClaimCore.ServerTests.PublicAssetTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Server
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

let private pressableStylesheet () =
    task {
        let bytes =
            Encoding.UTF8.GetBytes
                "[data-react-aria-pressable]{touch-action:pan-x pan-y pinch-zoom}"

        use host = new Host(true, buildAssets = [ "react-aria-pressable.css", bytes ])
        let admission = host.Services.GetRequiredService<AuthenticationAdmission>()
        use! _first = holdAuthentication admission (DefaultHttpContext())
        use! _second = holdAuthentication admission (DefaultHttpContext())
        use! response = host.Client.GetAsync "/react-aria-pressable.css"

        Expect.equal
            response.StatusCode
            HttpStatusCode.OK
            "The exact verified stylesheet stays public"

        let! actual = response.Content.ReadAsByteArrayAsync()
        Expect.equal actual bytes "Only the inventoried bytes are served"

        Expect.isTrue
            (response.Headers.CacheControl |> nonNull).NoStore
            "A fixed URL cannot retain immutable bytes across builds"

        Expect.isFalse (response.Headers.Contains "Set-Cookie") "Public CSS creates no session"
        use! unknown = host.Client.GetAsync "/another-pressable.css"

        Expect.equal
            (int unknown.StatusCode)
            429
            "Other root files gain no authentication exemption"
    }

let private brandMark () =
    task {
        let bytes = Encoding.UTF8.GetBytes "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"
        use host = new Host(true, buildAssets = [ "brand.svg", bytes ])
        use! response = host.Client.GetAsync "/brand.svg"
        Expect.equal response.StatusCode HttpStatusCode.OK "The inventoried mark is public"

        Expect.equal
            (response.Content.Headers.ContentType |> nonNull).MediaType
            "image/svg+xml"
            "The mark has its SVG media type"

        let! actual = response.Content.ReadAsByteArrayAsync()
        Expect.equal actual bytes "The route serves the inventoried bytes"
        Expect.isFalse (response.Headers.Contains "Set-Cookie") "Public mark creates no session"
    }

let private independentAdmission () =
    task {
        let bytes = Encoding.UTF8.GetBytes "export const publicValue = 2;"
        use host = new Host(true, buildAssets = [ "assets/public-23456789.js", bytes ])
        let! login, _ = host.Login()
        Expect.equal login 200 "A cookie session is established before pressure"
        let admission = host.Services.GetRequiredService<AuthenticationAdmission>()
        use! _first = holdAuthentication admission (DefaultHttpContext())
        use! _second = holdAuthentication admission (DefaultHttpContext())
        let! busy, _ = host.Bootstrap()
        Expect.equal busy 429 "Private session work still respects authentication admission"
        use! busyResponse = host.Client.GetAsync "/api/session"
        Expect.equal (int busyResponse.StatusCode) 429 "The host returns the rate-limit response"

        Expect.equal
            (busyResponse.Headers.RetryAfter |> nonNull).Delta
            (Nullable(TimeSpan.FromSeconds 1.))
            "Every host rate-limit response states the retry delay"

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

let private cookieStorageIsolation () =
    task {
        let mutable payload = ClaimCore.TestSupport.SessionResults.stored [||]
        let mutable reads = 0
        let mutable refuse = false

        let storage =
            { new ISessionStore with
                member _.Create(principal, bytes, _, _, _, _) =
                    payload <-
                        {
                            StoredSessionTicket.PrincipalKey =
                                ClaimCore.Domain.PrincipalKey.naturalKey principal
                            Payload = bytes
                        }

                    Task.FromResult(SessionChanges.unchanged (String.replicate 64 "a"))

                member _.Retrieve(_, _) =
                    reads <- reads + 1

                    if refuse then
                        Task.FromException<SessionChanges<StoredSessionTicket option>>(
                            AdmissionRejectedException()
                        )
                    else
                        Task.FromResult(SessionChanges.unchanged (Some payload))

                member _.Renew(_, _, _, _, _) =
                    Task.FromResult(SessionChanges.unchanged ())

                member _.Remove(_, _, _) =
                    Task.FromResult(SessionChanges.unchanged ())
            }

        let bytes = Encoding.UTF8.GetBytes "export const publicValue = 3;"

        use host =
            new Host(true, sessions = storage, buildAssets = [ "assets/public-34567890.js", bytes ])

        let! login, _ = host.Login()
        Expect.equal login 200 "A real protected cookie names a stored ticket"
        refuse <- true

        let! status, actual =
            host.Send(System.Net.Http.HttpMethod.Get, "/assets/public-34567890.js", None, [])

        Expect.equal status 200 "A cookie-bearing public asset cannot enter session storage"
        Expect.equal actual bytes "Only verified public content is served"
        Expect.equal reads 0 "No hidden automatic authentication runs before the public boundary"
        let! denied, _ = host.Bootstrap()

        Expect.equal
            denied
            429
            "Private authentication capacity refusal remains a structured transport result"

        Expect.equal reads 1 "The owned authentication stage actually enforces the storage boundary"
    }

let private mismatch path =
    let directory = Directory.CreateTempSubdirectory("claimcore-public-inventory-")

    try
        publishedInventory directory.FullName [ path, Encoding.UTF8.GetBytes "original" ]
        File.WriteAllText(Path.Combine(directory.FullName, path), "actual")

        let mutable refused = false

        try
            PublicAssets.Inventory directory.FullName |> ignore
        with WebStartupException WebStartupProblem.AssetsMissing ->
            refused <- true

        Expect.isTrue refused "An unreviewed replacement cannot borrow a public content identity"
    finally
        directory.Delete true

let private serveDocuments () =
    task {
        use host = new Host(true)

        let cases =
            [
                "/index.html", "text/html; charset=utf-8"
                "/auth-recovery.html", "text/html; charset=utf-8"
                "/LICENSE.txt", "text/plain; charset=utf-8"
                "/PROJECT-NOTICE.md", "text/markdown; charset=utf-8"
                "/LICENSE-COMMERCIAL.md", "text/markdown; charset=utf-8"
                "/THIRD-PARTY-NOTICES.txt", "text/plain; charset=utf-8"
                "/SOURCE.tar.gz", "application/gzip"
            ]

        for path, mediaType in cases do
            let file = Path.Combine(host.AssetsDirectory, path.TrimStart('/'))
            let expected = File.ReadAllBytes file
            File.WriteAllText(file, "different bytes")
            use! response = host.Client.GetAsync path
            Expect.equal response.StatusCode HttpStatusCode.OK path
            let! actual = response.Content.ReadAsByteArrayAsync()
            Expect.equal actual expected ("Captured bytes at " + path)
            Expect.equal ((nonNull response.Content.Headers.ContentType).ToString()) mediaType path

            Expect.equal
                response.Content.Headers.ContentLength
                (Nullable(int64 expected.Length))
                path

            Expect.isTrue (nonNull response.Headers.CacheControl).NoStore path

            if path = "/SOURCE.tar.gz" then
                let disposition = nonNull response.Content.Headers.ContentDisposition
                Expect.equal disposition.DispositionType "attachment" path
                Expect.equal disposition.FileName "SOURCE.tar.gz" path

            use request = new HttpRequestMessage(HttpMethod.Head, path)
            use! head = host.Client.SendAsync request
            let! body = head.Content.ReadAsByteArrayAsync()
            Expect.equal head.StatusCode HttpStatusCode.OK path
            Expect.equal head.Content.Headers.ContentLength (Nullable(int64 expected.Length)) path
            Expect.isEmpty body path

            use conditional = new HttpRequestMessage(HttpMethod.Get, path)
            conditional.Headers.IfNoneMatch.Add(nonNull response.Headers.ETag)
            use! cached = host.Client.SendAsync conditional
            Expect.equal cached.StatusCode HttpStatusCode.NotModified path

        for path in [ "/"; "/cases/CASE.001" ] do
            use! response = host.Client.GetAsync path
            Expect.equal response.StatusCode HttpStatusCode.OK path
            let! actual = response.Content.ReadAsStringAsync()
            Expect.equal actual "<!doctype html><title>Synthetic ClaimCore</title>" path
    }

let private rejectsUnlistedFiles () =
    task {
        use host = new Host(true)

        File.WriteAllText(Path.Combine(host.AssetsDirectory, "stray.txt"), "not inventoried")

        for path in [ "/public-assets.json"; "/stray.txt"; "/assets/"; "/locales/" ] do
            use request = new HttpRequestMessage(HttpMethod.Get, path)
            request.Headers.Accept.ParseAdd "text/html"
            use! response = host.Client.SendAsync request
            Expect.equal response.StatusCode HttpStatusCode.NotFound path
            let! body = response.Content.ReadAsStringAsync()
            Expect.stringContains body "ENDPOINT_MISSING" path
    }

let private missingDocument () =
    let directory = Directory.CreateTempSubdirectory("claimcore-required-documents-")

    try
        publishedInventory directory.FullName []
        let manifest = Path.Combine(directory.FullName, "public-assets.json")
        let root = JsonNode.Parse(File.ReadAllText manifest) |> nonNull
        let assets = (nonNull root["assets"]).AsArray()

        let document =
            assets
            |> Seq.find (fun entry ->
                let item = nonNull entry
                let path = item["path"] |> nonNull
                path.GetValue<string>() = "/SOURCE.tar.gz")

        assets.Remove document |> ignore
        File.WriteAllText(manifest, root.ToJsonString())
        let mutable refused = false

        try
            PublicAssets.Inventory directory.FullName |> ignore
        with WebStartupException WebStartupProblem.AssetsMissing ->
            refused <- true

        Expect.isTrue refused "A required legal document omitted from the manifest refuses startup"
    finally
        directory.Delete true

let private viewerPath = "/attachment-preview/viewer"

let private serveViewer () =
    task {
        use host = new Host(true)

        let csp (response: HttpResponseMessage) =
            response.Headers.GetValues "Content-Security-Policy" |> Seq.exactlyOne

        use! response = host.Client.GetAsync viewerPath
        Expect.equal response.StatusCode HttpStatusCode.OK "Dedicated viewer route"

        Expect.equal
            (csp response)
            ("sandbox allow-scripts; default-src 'none'; "
             + "script-src 'sha256-Oez3mJJV+hbsf5/1ENpn6ZL6yHiBCLK0YcbpTojVxw0=' 'wasm-unsafe-eval'; "
             + "style-src 'none'; "
             + "img-src blob: data:; connect-src 'none'; worker-src blob:; "
             + "frame-ancestors 'self'; base-uri 'none'; form-action 'none'")
            "The viewer has its own hash-pinned opaque sandbox"

        Expect.isFalse (response.Headers.Contains "X-Frame-Options") "The viewer can be framed"
        Expect.isTrue (response.Headers.CacheControl |> nonNull).NoStore "Fresh viewer navigation"
        Expect.isFalse (response.Headers.Contains "Set-Cookie") "No private session writes"
        let! served = response.Content.ReadAsByteArrayAsync()

        Expect.equal
            served
            (File.ReadAllBytes(Path.Combine(host.AssetsDirectory, "attachment-preview.viewer")))
            "The host serves the actual admitted bytes"

        use! workspace = host.Client.GetAsync "/"

        Expect.equal
            (csp workspace)
            ("default-src 'none'; script-src 'self'; style-src 'self'; style-src-attr 'none'; "
             + "img-src 'self' blob:; font-src 'self'; connect-src 'self'; object-src 'none'; "
             + "base-uri 'none'; frame-ancestors 'none'; "
             + "frame-src https://localhost:5443/attachment-preview/viewer; form-action 'self'; "
             + "manifest-src 'self'; media-src 'none'; worker-src 'none'; "
             + "require-trusted-types-for 'script'; trusted-types 'none'")
            "Workspace CSP permits just the configured viewer"

        Expect.equal
            (workspace.Headers.GetValues "X-Frame-Options" |> Seq.exactlyOne)
            "DENY"
            "Workspace retains frame denial"

        use! wrongMethod = host.Client.PostAsync(viewerPath, null)

        Expect.equal
            wrongMethod.StatusCode
            HttpStatusCode.MethodNotAllowed
            "Viewer serves read methods"

        use! alternate = host.Client.GetAsync "/attachment-preview.viewer"

        Expect.isFalse
            ((csp alternate).StartsWith("sandbox", StringComparison.Ordinal))
            "Build file has no viewer route"
    }

let private viewerMismatch tamper =
    let directory = Directory.CreateTempSubdirectory("claimcore-viewer-inventory-")

    try
        publishedInventory directory.FullName []
        tamper directory.FullName
        let mutable refused = false

        try
            PublicAssets.Inventory directory.FullName |> ignore
        with WebStartupException WebStartupProblem.AssetsMissing ->
            refused <- true

        Expect.isTrue refused "The startup inventory must refuse the altered viewer"
    finally
        directory.Delete true

let private changeInlineHash (key: string) directory =
    let manifest = Path.Combine(directory, "public-assets.json")
    let root = JsonNode.Parse(File.ReadAllText manifest) |> nonNull
    let viewer = root["viewer"] |> nonNull
    viewer[key] <- JsonValue.Create "unverified"
    File.WriteAllText(manifest, root.ToJsonString())

[<Tests>]
let tests =
    testList
        "public asset identity"
        [
            testCaseAsync
                "caches only actual served build bytes and preserves private fallback policy"
                (async { do! serve () |> Async.AwaitTask })
            testCaseAsync
                "verifies fixed-name pressable CSS without immutable caching or broader exemptions"
                (async { do! pressableStylesheet () |> Async.AwaitTask })
            testCaseAsync
                "brand mark uses the verified public asset inventory"
                (async { do! brandMark () |> Async.AwaitTask })
            testCaseAsync
                "serves verified public bytes independently of saturated authentication"
                (async { do! independentAdmission () |> Async.AwaitTask })
            testCaseAsync
                "keeps cookie-bearing public requests outside session storage and contains private capacity rejection"
                (async { do! cookieStorageIsolation () |> Async.AwaitTask })
            for path in [ "assets/public-12345678.js"; "auth-recovery.html"; "SOURCE.tar.gz" ] do
                testCase $"refuses an inventory that disagrees with the published {path}" (fun () ->
                    mismatch path)
            testCase
                "refuses omission of a required root document from the inventory"
                missingDocument
            testCaseAsync
                "serves the exact inventoried documents, metadata, and fallback bytes"
                (async { do! serveDocuments () |> Async.AwaitTask })
            testCaseAsync
                "hides the inventory, stray files, and asset directories"
                (async { do! rejectsUnlistedFiles () |> Async.AwaitTask })
            testCaseAsync
                "applies dedicated viewer and workspace security headers"
                (async { do! serveViewer () |> Async.AwaitTask })
            testCase "refuses a tampered viewer document" (fun () ->
                viewerMismatch (fun directory ->
                    File.AppendAllText(
                        Path.Combine(directory, "attachment-preview.viewer"),
                        "changed"
                    )))
            testCase
                "refuses a false script hash for an otherwise verified viewer document"
                (fun () -> viewerMismatch (changeInlineHash "scriptSha256"))
        ]
