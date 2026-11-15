module ClaimCore.ServerTests.AdmissionHttpTests

open System
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Server
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open System.Reflection
open System.Threading.RateLimiting
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
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
        use! fixture = OidcCoreFixture.createFor oidc.Issuer.AbsoluteUri "synthetic-human"

        let principal =
            PrincipalKey.human oidc.Issuer.AbsoluteUri "synthetic-human"
            |> Result.defaultWith (string >> failtest)

        let held = HeldReads((fixture.Actor principal).Store)

        let service actor =
            fixture.Actor actor |> Service.mapStore (fun _ -> held)

        use host =
            new Host(true, forActor = service, installationId = fixture.InstallationId)

        let observed = DateTimeOffset.UtcNow
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
        use! _first = holdAuthentication admission callback
        use! _second = holdAuthentication admission callback
        let! excess = admission.Run(callback, fun _ -> Task.FromResult())
        Expect.isNone excess "A case variant cannot evade callback admission"
        let! status, _ = host.Bootstrap()

        Expect.equal
            status
            200
            "Slow interactive authentication cannot occupy valid cookie validation capacity"

        let bearer = DefaultHttpContext()
        bearer.Request.Headers.Authorization <- "Bearer synthetic-invalid-token"
        let! bearerLease = admission.Run(bearer, fun _ -> Task.FromResult())
        Expect.isSome bearerLease "Bearer validation has an independent bounded budget"
    }

// Direct acquisition tests isolate the gate identified by the real-browser trace.
let private cookieQueue () =
    task {
        use budget = new AuthenticationAdmission(CancellationToken.None)
        let context () = DefaultHttpContext()
        use! first = holdAuthentication budget (context ())
        use! second = holdAuthentication budget (context ())
        let entered = ResizeArray<int>()

        let queued =
            [|
                for index in 0..7 ->
                    budget.Run(
                        context (),
                        fun _ ->
                            entered.Add index
                            Task.FromResult index
                    )
            |]

        Expect.isTrue
            (queued |> Array.forall (fun work -> not work.IsCompleted))
            "All eight wait without running callbacks"

        let! excess = budget.Run(context (), fun _ -> failtest "Full queue entered provider")
        Expect.isNone excess "The ninth waiter is rejected immediately"
        first.Dispose()
        let! values = Task.WhenAll queued
        Expect.equal (entered |> Seq.toList) [ 0..7 ] "Oldest queued requests are promoted first"

        Expect.equal
            (values |> Array.toList)
            ([ 0..7 ] |> List.map Some)
            "Every admitted callback completes"
        // Two live permits are retained; the first owner above has been released once.
        use! replacement = holdAuthentication budget (context ())

        let! expired =
            budget.Run(context (), fun _ -> failtest "Expired waiter entered provider")

        Expect.isNone expired "A finite wait expires without provider work"
        let bearer = context ()
        bearer.Request.Headers.Authorization <- "Bearer test"
        let! separate = budget.Run(bearer, fun _ -> Task.FromResult 1)
        Expect.equal separate (Some 1) "Cookie occupancy never spends bearer permits"
        replacement.Dispose()
        second.Dispose()
        let! after = budget.Run(context (), fun _ -> Task.FromResult 1)
        Expect.equal after (Some 1) "Expiration releases queue ownership"
    }

let private cancelledCookies () =
    task {
        for close in [ "request"; "host"; "dispose" ] do
            use stopping = new CancellationTokenSource()
            use abort = new CancellationTokenSource()
            use budget = new AuthenticationAdmission(stopping.Token)
            use! first = holdAuthentication budget (DefaultHttpContext())
            use! _second = holdAuthentication budget (DefaultHttpContext())
            let context = DefaultHttpContext()
            context.RequestAborted <- abort.Token
            let mutable called = false

            let queued =
                budget.Run(
                    context,
                    fun _ ->
                        called <- true
                        Task.FromResult()
                )

            match close with
            | "request" -> abort.Cancel()
            | "host" -> stopping.Cancel()
            | _ -> (budget :> IDisposable).Dispose()

            first.Dispose()
            let! result = queued.WaitAsync(TimeSpan.FromSeconds 3.)
            Expect.isNone result "Cancellation while waiting refuses acquisition"
            Expect.isFalse called "No cancelled callback reaches provider/session work"
        // A promoted owner outlives the queue deadline; only its cleanup releases capacity.
        use budget = new AuthenticationAdmission(CancellationToken.None)
        use! first = holdAuthentication budget (DefaultHttpContext())
        use! _second = holdAuthentication budget (DefaultHttpContext())
        do! Task.Delay(AdmissionPolicy.authenticationQueueWait + TimeSpan.FromMilliseconds 50.)
        let queued = holdAuthentication budget (DefaultHttpContext())
        Expect.isFalse queued.IsCompleted "Active work is still held after its acquisition deadline"
        first.Dispose()
        use! _promoted = queued
        _second.Dispose()
        use! _other = holdAuthentication budget (DefaultHttpContext())

        let! excess =
            budget.Run(DefaultHttpContext(), fun _ -> failtest "Cleanup lease released early")

        Expect.isNone excess "Promoted owner retains its lease through cleanup"
    }

// HttpContext reads its lifetime feature again only after the real acquired lease has
// passed the waiting-token check. This boundary probe adds no production scheduling hook.
let private promotedCookieTransfer () =
    task {
        for close in [ "request"; "host"; "dispose" ] do
            use stopping = new CancellationTokenSource()
            use abort = new CancellationTokenSource()
            use budget = new AuthenticationAdmission(stopping.Token)

            let field =
                typeof<AuthenticationAdmission>
                    .GetField("cookie", BindingFlags.Instance ||| BindingFlags.NonPublic)
                |> nonNull

            let limiter = (field.GetValue(budget) |> nonNull) :?> ConcurrencyLimiter
            let statistics () = limiter.GetStatistics() |> nonNull
            use! first = holdAuthentication budget (DefaultHttpContext())
            use! second = holdAuthentication budget (DefaultHttpContext())
            let context = DefaultHttpContext()
            let mutable reads = 0
            let mutable called = false

            context.Features.Set<IHttpRequestLifetimeFeature>(
                { new IHttpRequestLifetimeFeature with
                    member _.RequestAborted
                        with get () =
                            if Interlocked.Increment(&reads) = 2 then
                                let promoted = statistics ()

                                Expect.equal
                                    promoted.CurrentQueuedCount
                                    0L
                                    "The real limiter has removed the promoted waiter"

                                Expect.equal
                                    promoted.TotalSuccessfulLeases
                                    3L
                                    "Both holders and the promoted acquisition succeeded"

                                Expect.equal
                                    promoted.CurrentAvailablePermits
                                    0L
                                    "The promoted lease owns the released permit before callback transfer"

                                match close with
                                | "request" -> abort.Cancel()
                                | "host" -> stopping.Cancel()
                                | _ -> (budget :> IDisposable).Dispose()

                            abort.Token
                        and set _ = failtest "The gate must not replace the request lifetime token"

                    member _.Abort() = abort.Cancel()
                }
            )

            let queued =
                budget.Run(
                    context,
                    fun _ ->
                        called <- true
                        Task.FromResult()
                )

            Expect.equal
                (statistics ()).CurrentQueuedCount
                1L
                "The acquisition genuinely waits behind two holders"

            first.Dispose()
            let! result = queued.WaitAsync(TimeSpan.FromSeconds 3.)

            Expect.equal
                reads
                2
                "Cancellation was injected at the successful acquisition-to-callback boundary"

            Expect.isNone result "Observed cancellation or closure refuses callback ownership"

            Expect.isFalse
                called
                "No provider/session callback begins after observed transfer cancellation"

            if close <> "dispose" then
                Expect.equal
                    (statistics ()).CurrentAvailablePermits
                    1L
                    "The refused promoted lease released exactly one permit"

                second.Dispose()

                Expect.equal
                    (statistics ()).CurrentAvailablePermits
                    2L
                    "Both permits are recovered, without leaked or excess capacity"
            else
                // A closed limiter has no reusable capacity/statistics. Already active cleanup
                // still finishes; the shared owner release is checked by live-gate cases above.
                second.Dispose()

                Expect.throwsT<ObjectDisposedException>
                    (fun () -> statistics () |> ignore)
                    "Disposed admission has no live limiter capacity"

            if close = "request" then
                use! againFirst = holdAuthentication budget (DefaultHttpContext())
                use! againSecond = holdAuthentication budget (DefaultHttpContext())
                use queuedAbort = new CancellationTokenSource()
                let third = DefaultHttpContext()
                third.RequestAborted <- queuedAbort.Token

                let excess =
                    budget.Run(
                        third,
                        fun _ -> failtest "Double release created a third active permit"
                    )

                Expect.equal
                    (statistics ()).CurrentQueuedCount
                    1L
                    "Recovered admission still allows only two active callbacks"

                queuedAbort.Cancel()
                let! refused = excess
                Expect.isNone refused "The capacity control waiter cancels without entering"
                againFirst.Dispose()
                againSecond.Dispose()
            elif close = "dispose" then
                let! closed =
                    budget.Run(
                        DefaultHttpContext(),
                        fun _ -> failtest "Disposed admission reopened"
                    )

                Expect.isNone closed "Disposed admission remains closed"
    }

[<Tests>]
let tests =
    testList
        "HTTP admission pressure"
        [
            testCaseAsync
                "promoted cookie acquisition rechecks cancellation before callback transfer"
                (async { do! promotedCookieTransfer () |> Async.AwaitTask })
            testCaseAsync
                "ordinary cookie admission is finite FIFO and isolated"
                (async { do! cookieQueue () |> Async.AwaitTask })
            testCaseAsync
                "ordinary cookie admission observes cancellation and cleanup"
                (async { do! cancelledCookies () |> Async.AwaitTask })
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
