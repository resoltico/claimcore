module ClaimCore.WebTests.SpaRouteTests

open System.Net.Http
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.TestSupport.TestServerFixture

let private page = "<!doctype html><title>Synthetic ClaimCore</title>"

let private deepLinks () =
    task {
        use host = new Host(true)

        for path in
            [
                "/cases/CASE.001"
                "/cases/A%2FB"
                "/cases/~bmV3"
                "/cases/~Lg"
                "/cases/~Li4"
                "/cases/~fg"
                "/cases/~fmJtVjM"
                "/cases/CASE.001/acts/CLOSE"
                "/operations/10000000-0000-4000-8000-000000000001"
                "/authority/10000000-0000-4000-8000-000000000001"
            ] do
            let! status, bytes = host.Send(HttpMethod.Get, path, None, [])
            Expect.equal status 200 $"Deep reload {path}"
            Expect.equal (Encoding.UTF8.GetString bytes) page $"SPA index {path}"

        Expect.isEmpty host.HandlerStub.Calls "Document navigation never dispatches core work"
    }
    |> _.GetAwaiter().GetResult()

let private missingAssets () =
    task {
        use host = new Host(true)

        for path in [ "/assets/missing.js"; "/missing.css"; "/unknown.html" ] do
            let! status, bytes = host.Send(HttpMethod.Get, path, None, [])
            Expect.equal status 404 $"Missing static file {path}"
            Expect.notEqual (Encoding.UTF8.GetString bytes) page "Never disguise assets as HTML"
    }
    |> _.GetAwaiter().GetResult()

let private apiFailures () =
    task {
        use host = new Host(true)

        for path, expected in
            [ "/api/missing", 404; "/api/missing.endpoint", 404; "/api/case.get", 405 ] do
            let! status, bytes = host.Send(HttpMethod.Get, path, None, [])
            Expect.equal status expected $"Preserved API failure {path}"
            Expect.notEqual (Encoding.UTF8.GetString bytes) page "API failures never serve the SPA"

            if path <> "/api/missing.endpoint" then
                use payload = JsonDocument.Parse bytes

                let code =
                    payload.RootElement.GetProperty("hostFailure").GetProperty("code").GetString()

                let expected =
                    if status = 405 then
                        "METHOD_REJECTED"
                    else
                        "ENDPOINT_MISSING"

                Expect.equal code expected "Preserved typed API failure"

        Expect.isEmpty host.HandlerStub.Calls "Rejected API paths never dispatch core work"
    }
    |> _.GetAwaiter().GetResult()

[<Tests>]
let tests =
    testList
        "SPA host routes"
        [
            testCase "dotted and escaped references reload through the production host" deepLinks
            testCase "missing static assets retain file-not-found responses" missingAssets
            testCase "unknown API paths and registered wrong methods retain failures" apiFailures
        ]
