namespace ClaimCore.Cli

open System
open System.IO
open ClaimCore.Contract

/// The current request stays frozen until its complete reply is flushed.
type FrameDelivery(output: Stream, errors: Stream) =
    let gate = obj ()
    let mutable interrupted = false
    let mutable potentiallyChanged = false
    let mutable dispatched = false
    let mutable pending: Invocation option = None

    let advance action =
        lock gate (fun () ->
            if interrupted then
                raise (OperationCanceledException())
            else
                action ())

    let retryFrame () =
        lock gate (fun () ->
            match pending with
            | None -> ()
            | Some frame ->
                errors.Write(Frames.encodeInvocation frame)
                errors.WriteByte(byte '\n')
                errors.Flush())

    member _.BeginFrame() =
        advance (fun () ->
            potentiallyChanged <- false
            dispatched <- false
            pending <- None)

    member _.Prepare frame =
        advance (fun () ->
            pending <- Some frame
            potentiallyChanged <- Frames.retryingUncertain frame)

    member _.BeforeDispatch frame =
        advance (fun () ->
            potentiallyChanged <- Frames.endpoint frame |> Api.mutates
            dispatched <- true
            pending <- Some frame)

    member _.Write(reply: CliReply) =
        advance ignore
        output.Write reply.Bytes
        output.WriteByte(byte '\n')
        output.Flush()

        if reply.ExitCode = 4 then
            retryFrame ()

        lock gate (fun () -> potentiallyChanged <- reply.ExitCode = 4)

    member _.Interrupt() =
        lock gate (fun () ->
            interrupted <- true

            if potentiallyChanged then
                try
                    retryFrame ()
                with _ ->
                    ()

                4
            else
                130)

    member _.Failure(error: exn) =
        let uncertain, sent = lock gate (fun () -> potentiallyChanged, dispatched)

        let reply =
            if uncertain then
                CliReply.unknown (CliReply.local ClientFailure.Unavailable)
            elif error :? OperationCanceledException then
                CliReply.local ClientFailure.Cancelled
            else
                CliReply.local ClientFailure.Unexpected

        let reply =
            match pending with
            | Some frame ->
                CliReply.forAttempt
                    frame
                    (if sent then
                         DeliveryState.Uncertain
                     else
                         DeliveryState.NotSent)
                    reply
            | None -> reply

        try
            lock gate (fun () ->
                errors.Write reply.Bytes
                errors.WriteByte(byte '\n')

                if uncertain then
                    retryFrame ()

                errors.Flush())
        with _ ->
            ()

        reply.ExitCode
