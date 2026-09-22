module ClaimCore.WebTests.HostRouteTests

open ClaimCore.Contracts
open System
open System.Security.Cryptography
open System.Text
open Expecto
open Microsoft.AspNetCore.Http
open ClaimCore.Web
open ClaimCore.WebTests.RouteFixtures

let private bootstrap (secret: string) =
    {
        Path = "synthetic-private-path"
        Digest = secret |> Encoding.UTF8.GetBytes |> SHA256.HashData
    }

let private inputFailureTests () =
    let oversized = context "{}"

    let oversizedBody =
        HostRoutes.invalidLoginBody oversized HttpInputProblem.BodyTooLarge

    execute oversized oversizedBody |> ignore
    Expect.equal oversized.Response.StatusCode 413 "Login keeps its smaller independent body limit"

    let malformed = context "{}"

    let malformedBody =
        HostRoutes.invalidLoginBody malformed HttpInputProblem.InvalidJson

    execute malformed malformedBody |> ignore

    Expect.equal
        malformed.Response.StatusCode
        400
        "Invalid login JSON is a typed not-started host failure"

let private credentialTests () =
    let sessions = SessionRegistry(TimeSpan.FromMinutes(1.), TimeSpan.FromMinutes(2.))
    let request = context "{}"
    request.Request.Headers["X-ClaimCore-Antiforgery"] <- "header-token"

    let rejected =
        HostRoutes.authenticateLogin
            (bootstrap "synthetic-valid-credential")
            sessions
            request
            {
                Credential = "synthetic-wrong-credential"
                AntiforgeryToken = "header-token"
            }
        |> readResult

    execute request rejected |> ignore

    Expect.equal
        request.Response.StatusCode
        401
        "Incorrect credentials never create a browser session"

let tests =
    testList
        "Web session route boundaries"
        [
            testCase "[CC-WEB-001] applies exact login body-limit classifications" inputFailureTests
            testCase
                "[CC-WEB-001] requires matching CSRF and bootstrap credentials before session issue"
                credentialTests
        ]
