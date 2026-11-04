module ClaimCore.WebTests.RequesterHttpTests

open System
open System.Net.Http
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.TestServerFixture

let private claimsReachContext () =
    task {
        let installation = Guid.NewGuid()
        use memory = new MemoryStore.Store(installation, DateTimeOffset.UtcNow)

        let calendar =
            { new IBusinessCalendar with
                member _.TimeZoneId = "Etc/UTC"

                member _.DateAt instant =
                    DateOnly.FromDateTime instant.UtcDateTime
            }

        use host =
            new Host(
                true,
                installationId = installation,
                forActor = (Service.create "synthetic" installation memory calendar)
            )

        let! login, _ =
            host.Send(
                HttpMethod.Post,
                "/__test/login?name=Signed%20hint&preferred_username=signed-user&email=hint%40example.invalid",
                None,
                []
            )

        Expect.equal
            login
            200
            "Synthetic cookie identity is authenticated through the production handler"

        let! token = host.Token()
        let! status, payload = host.Call("context", Encoding.UTF8.GetBytes "{}", token)
        Expect.equal status 200 "Authenticated own context is available before registration"
        use doc = JsonDocument.Parse payload

        let requester =
            doc.RootElement.GetProperty("outcome").GetProperty("data").GetProperty("requester")

        Expect.equal
            (requester.GetProperty("kind").GetString())
            "NOT_REGISTERED"
            "Only context reveals caller registration state"

        Expect.equal
            (requester.GetProperty("claims").GetProperty("name").GetString())
            "Signed hint"
            "Verified cookie hints cross the real ApiRoutes service binding"

        let request = List.exactlyOne memory.AccessRequests

        Expect.equal
            (request.Claims.Email |> Option.map PartyName.format)
            (Some "hint@example.invalid")
            "The same admitted hint reaches the bounded operational cache"

        let! status, refused =
            host.Call("authority.requests", Encoding.UTF8.GetBytes "{\"limit\":50}", token)

        Expect.equal status 200 "Core refusal preserves the structured envelope"
        use failure = JsonDocument.Parse refused

        Expect.equal
            (failure.RootElement
                .GetProperty("outcome")
                .GetProperty("data")
                .GetProperty("code")
                .GetString())
            "RESOURCE_UNAVAILABLE"
            "Own context hints do not grant request-directory access"
    }

[<Tests>]
let tests =
    testCaseAsync
        "authenticated requester hints flow through cookie gates into own context and cache"
        (claimsReachContext () |> Async.AwaitTask)
