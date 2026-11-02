module ClaimCore.WebTests.AdmissionHttpTests

open System
open System.Diagnostics
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Web
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.TestSupport
open ClaimCore.TestSupport.TestServerFixture

/// Two held provider callbacks expose the HTTP admission boundary without timing a busy loop.
type private HeldReads(storage: IStore) =
    let entered =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable reads = 0
    member _.Entered = entered.Task
    member _.Release() = release.TrySetResult() |> ignore
    member _.Reads = Volatile.Read(&reads)

    interface IStore with
        member _.Read operation =
            task {
                let count = Interlocked.Increment(&reads)

                if count <= 2 then
                    if count = 2 then
                        entered.SetResult()

                    do! release.Task

                return! storage.Read operation
            }

        member _.Write operation = storage.Write operation

let private pressure () =
    task {
        let installation = Guid.NewGuid()

        let principal =
            PrincipalKey.human oidc.Issuer.AbsoluteUri "synthetic-human"
            |> Result.defaultWith (string >> failtest)

        // The memory fixture models the ledger's whole-microsecond database timestamps.
        let observed = DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)
        use memory = new MemoryStore.Store(installation, observed)

        let calendar =
            { new IBusinessCalendar with
                member _.TimeZoneId = "Etc/UTC"

                member _.DateAt instant =
                    DateOnly.FromDateTime instant.UtcDateTime
            }

        let service store actor =
            Service.create "synthetic" installation store calendar actor

        let! provisioned =
            Service.provisionOwner
                (service memory principal)
                (Guid.NewGuid())
                CancellationToken.None

        match provisioned with
        | Ok(Verdict.Proceed _) -> ()
        | _ -> failtest "The HTTP principal is independently registered and enabled"

        let held = HeldReads(memory)
        use host = new Host(true, forActor = service held, installationId = installation)
        let! login, _ = host.Login()
        Expect.equal login 200 "Cookie authentication control"
        let! token = host.Token()
        let body = Encoding.UTF8.GetBytes "{}"
        let first = host.Call("context", body, token)
        let second = host.Call("context", body, token)

        try
            do! held.Entered.WaitAsync(TimeSpan.FromSeconds 3.)
            let! rejected, _ = host.Call("context", body, token)

            Expect.equal
                rejected
                429
                "The third request from the same authenticated identity is rejected"

            Expect.equal held.Reads 2 "Rejected work never reaches a storage callback"
            let clock = Stopwatch.StartNew()

            let statusBody =
                JsonSerializer.SerializeToUtf8Bytes {| operationId = Guid.NewGuid() |}

            let! status, bytes =
                host.Call("operation.status", statusBody, token).WaitAsync(TimeSpan.FromSeconds 3.)

            Expect.equal
                status
                200
                "Passive recovery has reserved capacity while ordinary reads are held"

            use reply = JsonDocument.Parse bytes

            Expect.equal
                (reply.RootElement.GetProperty("outcome").GetProperty("kind").GetString())
                "UNRESOLVED"
                "Admission does not fabricate final nonacceptance"

            Console.WriteLine(
                $"HTTP pressure observation: heldReads=2; rejectedReads=1; passiveStatusMilliseconds={clock.Elapsed.TotalMilliseconds:F3}"
            )

            match Environment.GetEnvironmentVariable "CLAIMCORE_TEST_REPORT_DIRECTORY" with
            | null -> ()
            | directory ->
                IO.Directory.CreateDirectory directory |> ignore

                IO.File.WriteAllBytes(
                    IO.Path.Combine(directory, "admission-http.json"),
                    JsonSerializer.SerializeToUtf8Bytes
                        {|
                            provider = "memory"
                            heldReads = 2
                            rejectedReads = 1
                            rejectedStatus = 429
                            passiveStatus = 200
                            passiveStatusMilliseconds = clock.Elapsed.TotalMilliseconds
                        |}
                )
        finally
            held.Release()

        let! completed = Task.WhenAll(first, second)

        Expect.isTrue
            (completed |> Array.forall (fst >> (=) 200))
            "Previously admitted reads complete normally"
    }

let private authenticationIsolation () =
    task {
        use host = new Host(true)
        let! login, _ = host.Login()
        Expect.equal login 200 "Valid cookie control before authentication pressure"
        let admission = host.Services.GetRequiredService<AuthenticationAdmission>()
        let callback = DefaultHttpContext()
        callback.Request.Path <- PathString "/SIGNIN-OIDC"
        use first = admission.Acquire callback
        use second = admission.Acquire callback
        use excess = admission.Acquire callback

        Expect.isTrue
            (first.IsAcquired && second.IsAcquired)
            "The bounded callback workload is held"

        Expect.isFalse excess.IsAcquired "A case variant cannot evade callback admission"
        let! status, _ = host.Bootstrap()

        Expect.equal
            status
            200
            "Slow interactive authentication cannot occupy valid cookie validation capacity"

        let bearer = DefaultHttpContext()
        bearer.Request.Headers.Authorization <- "Bearer synthetic-invalid-token"
        use bearerLease = admission.Acquire bearer
        Expect.isTrue bearerLease.IsAcquired "Bearer validation has an independent bounded budget"
    }

[<Tests>]
let tests =
    testList
        "HTTP admission pressure"
        [
            testCaseAsync
                "preserves passive recovery under one noisy principal's held reads"
                (async { do! pressure () |> Async.AwaitTask })
            testCaseAsync
                "isolates interactive authentication from cookie and bearer validation"
                (async { do! authenticationIsolation () |> Async.AwaitTask })
        ]
