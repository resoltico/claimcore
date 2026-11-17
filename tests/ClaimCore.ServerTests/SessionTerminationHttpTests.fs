module ClaimCore.ServerTests.SessionTerminationHttpTests

open System
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.TestSupport
open ClaimCore.TestSupport.TestServerFixture
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options

[<Tests>]
let tests =
    testCaseAsync
        "confirmed self termination expires the browser cookie once and suppresses queued sliding renewal"
        (async {
            use! fixture =
                OidcCoreFixture.createFor oidc.Issuer.AbsoluteUri "synthetic-human"
                |> Async.AwaitTask

            let mutable renewals = 0
            let mutable removals = 0
            let storage = fixture.Sessions

            let counted =
                { new ISessionStore with
                    member _.Create(principal, payload, expiry, idle, absolute, token) =
                        storage.Create(principal, payload, expiry, idle, absolute, token)

                    member _.Retrieve(key, token) = storage.Retrieve(key, token)

                    member _.Renew(key, principal, payload, validate, token) =
                        renewals <- renewals + 1
                        storage.Renew(key, principal, payload, validate, token)

                    member _.Remove(key, reason, token) =
                        removals <- removals + 1
                        storage.Remove(key, reason, token)
                }

            use host =
                new Host(
                    true,
                    forActor = fixture.Actor,
                    installationId = fixture.InstallationId,
                    sessions = counted
                )

            let! status, _ = host.Login() |> Async.AwaitTask
            Expect.equal status 200 "Real protected cookie issued"
            let! token = host.Token() |> Async.AwaitTask

            let options =
                host.Services
                    .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                    .Get(CookieAuthenticationDefaults.AuthenticationScheme)

            options.Events.OnCheckSlidingExpiration <-
                Func<CookieSlidingExpirationContext, Task>(fun context ->
                    context.ShouldRenew <- true
                    Task.CompletedTask)

            let before = fixture.EntryCount

            use! response =
                host.CallResponse(
                    "session.terminateOwn",
                    Encoding.UTF8.GetBytes "{}",
                    token,
                    representation = Representation.Human
                )
                |> Async.AwaitTask

            Expect.equal (int response.StatusCode) 200 "Operational response"
            let! bytes = response.Content.ReadAsByteArrayAsync() |> Async.AwaitTask
            use payload = JsonDocument.Parse bytes
            let outcome = payload.RootElement.GetProperty("outcome")
            Expect.equal (outcome.GetProperty("kind").GetString()) "SUCCEEDED" "Confirmed result"

            Expect.isTrue
                (outcome.GetProperty("self").GetBoolean())
                "Core identifies current principal"

            Expect.isFalse
                (payload.RootElement.TryGetProperty("operationId") |> fst)
                "No ledger proof is invented"

            Expect.equal
                fixture.SessionCount
                0L
                "All browser tickets deleted in the confirmed transaction"

            Expect.equal fixture.EntryCount before "Session deletion does not append history"
            Expect.equal removals 0 "Sign-out callback does not repeat committed storage deletion"
            Expect.equal renewals 0 "Queued sliding renewal is suppressed by actual cookie sign-out"
            let cookies = response.Headers.GetValues("Set-Cookie") |> Seq.toList

            Expect.isTrue
                (cookies
                 |> List.exists (fun value ->
                     value.StartsWith("__Host-ClaimCoreSession=;", StringComparison.Ordinal)))
                "Configured authentication cookie is expired"

            let! _, session = host.Bootstrap() |> Async.AwaitTask
            use anonymous = JsonDocument.Parse session

            Expect.isFalse
                (anonymous.RootElement
                    .GetProperty("outcome")
                    .GetProperty("data")
                    .GetProperty("authenticated")
                    .GetBoolean())
                "Current browser is anonymous"

            let! rejected, _ =
                host.Call("context", Encoding.UTF8.GetBytes "{}", token) |> Async.AwaitTask

            Expect.equal rejected 401 "Old cookie cannot reach private core context"
            let! _ = host.Login() |> Async.AwaitTask
            let! _, session = host.Bootstrap() |> Async.AwaitTask
            use fresh = JsonDocument.Parse session

            Expect.isTrue
                (fresh.RootElement
                    .GetProperty("outcome")
                    .GetProperty("data")
                    .GetProperty("authenticated")
                    .GetBoolean())
                "A fresh post-commit login remains permitted"
        })
