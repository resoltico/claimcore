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
        let storage = HandlerStub.ScriptedStore(DateTimeOffset.UtcNow, None)

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
                forActor = (Service.create "synthetic" installation storage calendar)
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

        let request = List.exactlyOne storage.ContextQueries

        Expect.equal
            (request.Claims.Email |> Option.map PartyName.format)
            (Some "hint@example.invalid")
            "The same admitted hint reaches the store context boundary"

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

let private activeIssuer () =
    task {
        let installation = Guid.NewGuid()
        let observed = DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)

        let principal =
            PrincipalKey.human oidc.Issuer.AbsoluteUri "synthetic-human"
            |> Result.defaultWith (string >> failtest)

        let actor =
            {
                Actor.ActorId = CaseFixtures.actorId (Guid.NewGuid())
                Principal = principal
                Enabled = true
                Profile = None
            }

        let storage = HandlerStub.ScriptedStore(observed, Some actor)

        let calendar =
            { new IBusinessCalendar with
                member _.TimeZoneId = "Etc/UTC"

                member _.DateAt instant =
                    DateOnly.FromDateTime instant.UtcDateTime
            }

        let service principal =
            Service.create "synthetic" installation storage calendar principal

        use host = new Host(true, installationId = installation, forActor = service)

        let! _, _ =
            host.Send(
                HttpMethod.Post,
                "/__test/login?issuer=https%3A%2F%2Fattacker.invalid%2F",
                None,
                []
            )

        let! token = host.Token()
        let! status, payload = host.Call("context", Encoding.UTF8.GetBytes "{}", token)
        Expect.equal status 200 "Authenticated ACTIVE context is available"
        use doc = JsonDocument.Parse payload

        let requester =
            doc.RootElement.GetProperty("outcome").GetProperty("data").GetProperty("requester")

        Expect.equal
            (requester.GetProperty("kind").GetString())
            "ACTIVE"
            "Only the admitted own account gets ACTIVE fields"

        Expect.equal
            (requester.GetProperty("issuer").GetString())
            oidc.Issuer.AbsoluteUri
            "Issuer comes from configured authentication authority, never request hints or guessed directory data"

        Expect.notEqual
            (requester.GetProperty("issuer").GetString())
            "https://attacker.invalid/"
            "Untrusted request input cannot select the authenticated issuer"
    }

[<Tests>]
let tests =
    testList
        "own requester authentication facts"
        [
            testCaseAsync
                "ACTIVE issuer is the configured authenticated authority"
                (activeIssuer () |> Async.AwaitTask)
            testCaseAsync
                "authenticated requester hints flow through cookie gates into own context and the store boundary"
                (claimsReachContext () |> Async.AwaitTask)
        ]
