module ClaimCore.WebTests.SessionKeyTests

open ClaimCore.TestSupport

open System
open System.IO
open System.Security.Claims
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Postgres
open ClaimCore.Web
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Http

let private ticket () =
    AuthenticationTicket(
        ClaimsPrincipal(ClaimsIdentity([ Claim("sub", "synthetic") ], "oidc")),
        "Cookies"
    )

let private adapter storage provider =
    OidcTicketStore(TimeSpan.FromMinutes 30., TimeSpan.FromHours 8., storage, provider, ignore)
    :> ITicketStore

let private awaited () =
    task {
        let pending =
            TaskCompletionSource<SessionChanges<byte array option>>(
                TaskCreationOptions.RunContinuationsAsynchronously
            )

        let storage =
            { new ISessionStore with
                member _.Create(_, _, _, _, _) =
                    Task.FromResult(SessionChanges.unchanged "synthetic-key")

                member _.Retrieve(_, _) = pending.Task

                member _.Renew(_, _, _) =
                    Task.FromResult(SessionChanges.unchanged ())

                member _.Remove(_, _, _) =
                    Task.FromResult(SessionChanges.unchanged ())
            }

        let store = adapter storage (EphemeralDataProtectionProvider())
        let retrieval = store.RetrieveAsync "synthetic-key"
        Expect.isFalse retrieval.IsCompleted "Retrieval awaits actual storage completion"
        pending.SetResult(SessionChanges.unchanged None)
        let! found = retrieval
        Expect.isNull found "Missing session refuses authentication"
    }

let private protectionFailures () =
    task {
        let mutable payload = [||]
        let mutable removed = false

        let storage =
            { new ISessionStore with
                member _.Create(bytes, _, _, _, _) =
                    payload <- bytes
                    Task.FromResult(SessionChanges.unchanged "synthetic-key")

                member _.Retrieve(_, _) =
                    Task.FromResult(SessionChanges.unchanged (Some payload))

                member _.Renew(_, _, _) =
                    Task.FromResult(SessionChanges.unchanged ())

                member _.Remove(_, _, _) =
                    removed <- true
                    Task.FromResult(SessionChanges.unchanged ())
            }

        let diagnostics = ResizeArray<string>()

        let first =
            OidcTicketStore(
                TimeSpan.FromMinutes 30.,
                TimeSpan.FromHours 8.,
                storage,
                EphemeralDataProtectionProvider(),
                diagnostics.Add
            )
            :> ITicketStore

        let! key = first.StoreAsync(ticket ())
        let! recovered = first.RetrieveAsync key
        Expect.isNotNull recovered "Protected ticket round trips"

        let second =
            OidcTicketStore(
                TimeSpan.FromMinutes 30.,
                TimeSpan.FromHours 8.,
                storage,
                EphemeralDataProtectionProvider(),
                diagnostics.Add
            )
            :> ITicketStore

        let! lost = second.RetrieveAsync key
        Expect.isNull lost "Key loss fails closed"
        Expect.isTrue removed "Unprotectable row is invalidated"

        Expect.sequenceEqual
            diagnostics
            [ "OIDC_SESSION_UNREADABLE" ]
            "Malformed protected data has a safe distinct diagnostic"
    }

let private keyContinuity () =
    task {
        let path = PrivateTestPaths.newPrivateDirectory "claimcore-key-continuity-"

        try
            let mutable payload = [||]

            let storage =
                { new ISessionStore with
                    member _.Create(bytes, _, _, _, _) =
                        payload <- bytes
                        Task.FromResult(SessionChanges.unchanged "synthetic-key")

                    member _.Retrieve(_, _) =
                        Task.FromResult(SessionChanges.unchanged (Some payload))

                    member _.Renew(_, _, _) =
                        Task.FromResult(SessionChanges.unchanged ())

                    member _.Remove(_, _, _) =
                        Task.FromResult(SessionChanges.unchanged ())
                }

            let first = adapter storage (DataProtectionProvider.Create(DirectoryInfo path))
            let! key = first.StoreAsync(ticket ())
            let restarted = adapter storage (DataProtectionProvider.Create(DirectoryInfo path))
            let! found = restarted.RetrieveAsync key
            Expect.isNotNull found "A fresh provider with the same key ring decrypts the session"
        finally
            Directory.Delete(path, true)
    }

let private storageFailure () =
    task {
        let storage =
            { new ISessionStore with
                member _.Create(_, _, _, _, _) =
                    Task.FromException<SessionChanges<string>>(
                        IOException "Synthetic database failure"
                    )

                member _.Retrieve(_, _) =
                    Task.FromException<SessionChanges<byte array option>>(
                        IOException "Synthetic database failure"
                    )

                member _.Renew(_, _, _) =
                    Task.FromException<SessionChanges<unit>>(
                        IOException "Synthetic database failure"
                    )

                member _.Remove(_, _, _) =
                    Task.FromException<SessionChanges<unit>>(
                        IOException "Synthetic database failure"
                    )
            }

        let diagnostics = ResizeArray<string>()

        let store =
            OidcTicketStore(
                TimeSpan.FromMinutes 30.,
                TimeSpan.FromHours 8.,
                storage,
                EphemeralDataProtectionProvider(),
                diagnostics.Add
            )
            :> ITicketStore

        let! found = store.RetrieveAsync "synthetic"
        Expect.isNull found "Database I/O failure authenticates nobody"

        Expect.sequenceEqual
            diagnostics
            [ "OIDC_SESSION_UNAVAILABLE" ]
            "Database failure has a safe distinct diagnostic"
    }

let private requestAbort () =
    task {
        let mutable observed = CancellationToken.None

        let storage =
            { new ISessionStore with
                member _.Create(_, _, _, _, _) =
                    Task.FromResult(SessionChanges.unchanged "synthetic")

                member _.Retrieve(_, token) =
                    task {
                        observed <- token
                        do! Task.Delay(Timeout.Infinite, token)
                        return SessionChanges.unchanged None
                    }

                member _.Renew(_, _, _) =
                    Task.FromResult(SessionChanges.unchanged ())

                member _.Remove(_, _, _) =
                    Task.FromResult(SessionChanges.unchanged ())
            }

        let store = adapter storage (EphemeralDataProtectionProvider())
        use aborted = new CancellationTokenSource()
        let context = DefaultHttpContext()
        context.RequestAborted <- aborted.Token
        let waiting = store.RetrieveAsync("synthetic", context, CancellationToken.None)
        Expect.isFalse waiting.IsCompleted "An active request awaits the actual storage operation"
        aborted.Cancel()
        let! found = waiting
        Expect.isNull found "Request abort fails authentication closed"

        Expect.isTrue
            observed.IsCancellationRequested
            "The session port receives the context's cancellation"
    }

let private keyDirectory () =
    let path = PrivateTestPaths.newPrivateDirectory "claimcore-key-policy-"

    try
        SessionKeys.admit path |> ignore

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead
            ||| UnixFileMode.UserWrite
            ||| UnixFileMode.UserExecute
            ||| UnixFileMode.GroupRead
        )

        Expect.throws
            (fun () -> SessionKeys.admit path |> ignore)
            "Group-readable key directories refuse"
    finally
        Directory.Delete(path, true)

[<Tests>]
let tests =
    testList
        "Database session adapter"
        [
            testCaseAsync
                "session retrieval awaits storage"
                (async { do! awaited () |> Async.AwaitTask })
            testCaseAsync
                "key loss invalidates protected tickets"
                (async { do! protectionFailures () |> Async.AwaitTask })
            testCaseAsync
                "key ring survives restart"
                (async { do! keyContinuity () |> Async.AwaitTask })
            testCaseAsync
                "database failure fails closed"
                (async { do! storageFailure () |> Async.AwaitTask })
            testCaseAsync
                "request abort reaches session storage"
                (async { do! requestAbort () |> Async.AwaitTask })
            testCase "key directory is owner-only" keyDirectory
        ]
