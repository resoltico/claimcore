module ClaimCore.WebTests.QueuedCapacityTests

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.WebTests.TestServerFixture

type private CaseReadOutcome = QueryOutcome<Lookup<CurrentCase, string>>

let private holdCore (host: Host) =
    let started = ConcurrentDictionary<string, unit>()
    let pending = ConcurrentBag<TaskCompletionSource<CaseReadOutcome>>()
    let mutable hold = true
    let gate = obj ()

    host.Runtime.GetWork <-
        Some(fun reference _ ->
            lock gate (fun () ->
                started.TryAdd(reference, ()) |> ignore

                if not hold then
                    Task.FromResult(QueryOutcome.Succeeded(Lookup.NotFound reference))
                else
                    let signal =
                        TaskCompletionSource<CaseReadOutcome>(
                            TaskCreationOptions.RunContinuationsAsynchronously
                        )

                    pending.Add(signal)
                    signal.Task))

    let release () =
        lock gate (fun () ->
            hold <- false

            for signal in pending do
                signal.TrySetResult(QueryOutcome.Succeeded(Lookup.NotFound "SYNTHETIC"))
                |> ignore)

    started, release

let private requests (host: Host) token (cancellations: CancellationTokenSource array) =
    cancellations
    |> Array.mapi (fun i cancellation ->
        let reference = sprintf "QUEUE-%02d" i
        let body = sprintf "{\"caseReference\":\"%s\"}" reference

        reference,
        host.SendAsync(
            HttpMethod.Post,
            "/api/v3/cases/get",
            Some body,
            Some "application/json",
            Some token,
            cancellation.Token
        ))


let private queuedOverloadAndCancellation () =
    use host = Host.Start()
    host.Login() |> ignore
    let token = host.SessionToken()
    let started, release = holdCore host
    let cancellations = [| for _ in 1..21 -> new CancellationTokenSource() |]

    let requests = requests host token cancellations

    try
        let first =
            Task
                .WhenAny(requests |> Array.map snd)
                .WaitAsync(TimeSpan.FromSeconds 5.)
                .GetAwaiter()
                .GetResult()
                .GetAwaiter()
                .GetResult()

        Expect.equal first.Status 429 "Four active plus sixteen queued requests refuse overload."
        use refusal = document first

        Expect.equal
            (refusal.RootElement.GetProperty("code").GetString())
            "WEB_BUSY"
            "Overload is a typed host refusal."

        Expect.equal
            (refusal.RootElement.GetProperty("executionPhase").GetString())
            "NOT_STARTED"
            "Rejected overload never dispatched."

        Expect.equal started.Count 4 "Only the active permit holders reached core."

        let index =
            requests
            |> Array.findIndex (fun (reference, task) ->
                not task.IsCompleted && not (started.ContainsKey(reference)))

        cancellations[index].Cancel()

        try
            (snd requests[index]).GetAwaiter().GetResult() |> ignore
            failtest "Queued request must report cancellation."
        with :? OperationCanceledException ->
            ()

        Expect.isFalse
            (started.ContainsKey(fst requests[index]))
            "Canceled queued read never reaches the facade."
    finally
        release ()

        for _, task in requests do
            try
                task.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult() |> ignore
            with :? OperationCanceledException ->
                ()

        for cancellation in cancellations do
            cancellation.Dispose()

let tests =
    testList
        "queued HTTP capacity"
        [
            testCase
                "[CC-WEB-001] bounded admission refuses overload and cancels queued reads before dispatch"
                queuedOverloadAndCancellation
        ]
