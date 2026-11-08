module ClaimCore.WebTests.AdmissionHttpTests

open System
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
        member _.Context operation =
            task {
                let count = Interlocked.Increment(&reads)

                if count <= 2 then
                    if count = 2 then
                        entered.SetResult()

                    do! release.Task

                return! storage.Context operation
            }

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

let private waitQueued (admission: PrincipalAdmission) count =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 3.)

        while admission.QueuedReads <> count do
            do! Task.Delay(10, deadline.Token)
    }

let private busy (response: System.Net.Http.HttpResponseMessage) =
    task {
        Expect.equal (int response.StatusCode) 429 "Saturation is an HTTP busy reply"

        Expect.equal
            (response.Headers.RetryAfter |> nonNull).Delta
            (Nullable(TimeSpan.FromSeconds 1.))
            "The server supplies Retry-After: 1"

        let! bytes = response.Content.ReadAsByteArrayAsync()
        use body = JsonDocument.Parse bytes

        Expect.equal
            (body.RootElement.GetProperty("hostFailure").GetProperty("code").GetString())
            "RATE_LIMITED"
            "Capacity is not reported as an authentication failure"
    }

let private pressure () =
    task {
        let installation = Guid.NewGuid()

        let principal =
            PrincipalKey.human oidc.Issuer.AbsoluteUri "synthetic-human"
            |> Result.defaultWith (string >> failtest)

        let observed = DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)

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

        let service store actor =
            Service.create "synthetic" installation store calendar actor

        let held = HeldReads(storage)
        use host = new Host(true, forActor = service held, installationId = installation)
        let! login, _ = host.Login()
        Expect.equal login 200 "Cookie authentication control"
        let! token = host.Token()
        let body = Encoding.UTF8.GetBytes "{}"
        let first = host.Call("context", body, token)
        let second = host.Call("context", body, token)

        let admission = host.Services.GetRequiredService<PrincipalAdmission>()
        use cancelled = new CancellationTokenSource()
        let queued = ResizeArray<Task<System.Net.Http.HttpResponseMessage>>()

        try
            do! held.Entered.WaitAsync(TimeSpan.FromSeconds 3.)
            use! timedOut = host.CallResponse("context", body, token)
            do! busy timedOut
            Expect.equal held.Reads 2 "A timed-out queued request never reaches storage"

            for index in 0..7 do
                queued.Add(
                    host.CallResponse(
                        "context",
                        body,
                        token,
                        cancellationToken =
                            (if index = 3 then
                                 cancelled.Token
                             else
                                 CancellationToken.None)
                    )
                )

                do! waitQueued admission (int64 (index + 1))

            use! excess = host.CallResponse("context", body, token)
            do! busy excess
            Expect.equal held.Reads 2 "Queued and excess requests never reach storage"

            let statusBody =
                JsonSerializer.SerializeToUtf8Bytes
                    {|
                        operationId = Guid.CreateVersion7(observed)
                    |}

            use! lookup = host.CallResponse("operation.get", statusBody, token)
            do! busy lookup

            cancelled.Cancel()

            let! aborted =
                task {
                    try
                        use! _response = queued[3]
                        return false
                    with :? OperationCanceledException ->
                        return true
                }

            Expect.isTrue aborted "Client abort propagates through HTTP queue acquisition"
            do! waitQueued admission 7L
            Expect.equal held.Reads 2 "An aborted queued read never enters its action"
        finally
            held.Release()

        for index in 0..7 do
            if index <> 3 then
                use! response = queued[index]

                Expect.equal
                    (int response.StatusCode)
                    200
                    "Queued reads proceed after capacity is released"

        let! completed = Task.WhenAll(first, second)

        Expect.isTrue
            (completed |> Array.forall (fst >> (=) 200))
            "Previously admitted reads complete normally"

        let statusBody =
            JsonSerializer.SerializeToUtf8Bytes
                {|
                    operationId = Guid.CreateVersion7(observed)
                |}

        let! status, bytes = host.Call("operation.get", statusBody, token)
        Expect.equal status 200 "A released read slot admits lookup"
        use reply = JsonDocument.Parse bytes

        Expect.equal
            (reply.RootElement.GetProperty("outcome").GetProperty("kind").GetString())
            "PENDING"
            "Read admission never fabricates final absence"
    }

let private confirmationBudget () =
    task {
        use! fixture = OidcCoreFixture.createFor oidc.Issuer.AbsoluteUri "synthetic-human"
        let writes = System.Collections.Concurrent.ConcurrentQueue<unit>()

        let forActor principal =
            fixture.Actor principal
            |> Service.mapStore (fun storage ->
                { new IStore with
                    member _.Context query = storage.Context query
                    member _.Read operation = storage.Read operation

                    member _.Write operation =
                        writes.Enqueue()
                        storage.Write operation
                })

        use host =
            new Host(true, forActor = forActor, installationId = fixture.InstallationId)

        let! _ = host.Login()
        let! token = host.Token()

        let principal =
            PrincipalKey.human oidc.Issuer.AbsoluteUri "synthetic-human"
            |> Result.defaultWith (string >> failtest)

        let admission = host.Services.GetRequiredService<PrincipalAdmission>()

        let! acquired =
            admission.Acquire(principal, RequestWorkload.Write, CancellationToken.None)

        use writer =
            acquired |> Option.defaultWith (fun () -> failtest "Uncontended writer control")

        let before = fixture.EntryCount

        let pending =
            JsonSerializer.SerializeToUtf8Bytes
                {|
                    operationId = Guid.CreateVersion7()
                |}

        let! pendingStatus, pendingBytes = host.Call("operation.get", pending, token)
        Expect.equal pendingStatus 200 "Snapshot lookup does not need a write permit"
        use pendingReply = JsonDocument.Parse pendingBytes

        Expect.equal
            (pendingReply.RootElement.GetProperty("outcome").GetProperty("kind").GetString())
            "PENDING"
            "Current absence stays provisional"

        let expired =
            JsonSerializer.SerializeToUtf8Bytes
                {|
                    operationId = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(-11.))
                |}

        use! blocked = host.CallResponse("operation.get", expired, token)
        do! busy blocked
        Expect.equal writes.Count 0 "Saturated confirmation never enters storage Write"
        writer.Dispose()
        let! status, bytes = host.Call("operation.get", expired, token)
        Expect.equal status 200 "Released write capacity permits locked confirmation"
        use reply = JsonDocument.Parse bytes

        Expect.equal
            (reply.RootElement.GetProperty("outcome").GetProperty("kind").GetString())
            "NOT_ACCEPTED"
            "Final absence is confirmed"

        Expect.equal writes.Count 1 "Only the expired absence uses the write budget"
        Expect.equal fixture.EntryCount before "Neither lookup appends an acceptance or control row"
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
                "budgets only locked operation lookup confirmation as a write"
                (async { do! confirmationBudget () |> Async.AwaitTask })
            testCaseAsync
                "queues reads, bounds lookup, cancels disconnects and returns retry guidance"
                (async { do! pressure () |> Async.AwaitTask })
            testCaseAsync
                "isolates interactive authentication from cookie and bearer validation"
                (async { do! authenticationIsolation () |> Async.AwaitTask })
        ]
