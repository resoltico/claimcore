module ClaimCore.Tests.ShellConsoleFixture

open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open ClaimCore.Cli

/// Explicit producer/consumer rendezvous, without a second reader or timing sleeps.
type internal Terminal(lines: ShellLine list) =
    let gate = obj ()
    let queued = Queue<ShellLine>(lines)
    let prompts = Dictionary<int, TaskCompletionSource<unit>>()
    let mutable pending: TaskCompletionSource<ShellLine> option = None
    let mutable reads = 0
    let mutable hadText = false
    let output = new MemoryStream()
    let errors = new MemoryStream()

    let deliver line =
        match pending with
        | Some value ->
            pending <- None
            value.TrySetResult line |> ignore
        | None -> queued.Enqueue line

    let read _ _ =
        lock gate (fun () ->
            reads <- reads + 1

            match prompts.TryGetValue reads with
            | true, signal -> signal.TrySetResult() |> ignore
            | _ -> ()

            if queued.Count > 0 then
                Task.FromResult(queued.Dequeue())
            else
                let receiver =
                    TaskCompletionSource<ShellLine>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    )

                pending <- Some receiver
                receiver.Task)

    member _.Output = output
    member _.Errors = errors
    member _.Submit line = lock gate (fun () -> deliver line)
    member _.BufferedText(value) = hadText <- value

    member _.AtRead(count) =
        lock gate (fun () ->
            if reads >= count then
                Task.CompletedTask
            else
                let signal =
                    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

                prompts.Add(count, signal)
                signal.Task :> Task)
        |> fun ready -> ready.WaitAsync(TimeSpan.FromSeconds 5.)

    member _.Console =
        {
            Read = read
            Interrupt =
                fun () ->
                    lock gate (fun () ->
                        if pending.IsSome then
                            deliver (ShellLine.Interrupted hadText)
                            hadText <- false)
            Output = output
            Errors = errors
            ReviewInteractive = true
        }

    interface IDisposable with
        member _.Dispose() =
            output.Dispose()
            errors.Dispose()
