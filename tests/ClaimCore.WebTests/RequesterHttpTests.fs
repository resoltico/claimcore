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

let private activeIssuer () =
    task {
        let installation = Guid.NewGuid()
        // The memory fixture models the ledger's whole-microsecond database timestamps.
        let observed = DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)
        use memory = new MemoryStore.Store(installation, observed)

        let principal =
            PrincipalKey.human oidc.Issuer.AbsoluteUri "synthetic-human"
            |> Result.defaultWith (string >> failtest)

        let calendar =
            { new IBusinessCalendar with
                member _.TimeZoneId = "Etc/UTC"

                member _.DateAt instant =
                    DateOnly.FromDateTime instant.UtcDateTime
            }

        let service principal =
            Service.create "synthetic" installation memory calendar principal

        let! provisioned =
            Service.provisionOwner
                (service principal)
                (Guid.NewGuid())
                Inputs.ownerProfile
                System.Threading.CancellationToken.None

        match provisioned with
        | Ok(Verdict.Proceed _) -> ()
        | other -> failtestf "Authored initial owner: %A" other

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
                "authenticated requester hints flow through cookie gates into own context and cache"
                (claimsReachContext () |> Async.AwaitTask)
        ]
