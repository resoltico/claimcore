namespace ClaimCore.Cli

open ClaimCore.Wire

open System
open System.IO

/// A successful flush clears the current frame's dispatch knowledge.
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

            action ())

    member _.BeginFrame() =
        advance (fun () ->
            potentiallyChanged <- false
            dispatched <- false
            pending <- None)

    member _.Prepare frame =
        advance (fun () -> pending <- Some frame)

    member _.BeforeDispatch frame =
        advance (fun () ->
            potentiallyChanged <-
                (Frames.request frame |> WireRequest.endpoint |> Endpoint.mutates)

            dispatched <- true
            pending <- Some frame)

    member _.Write(reply: CliReply) =
        advance ignore
        output.Write reply.Bytes
        output.WriteByte(byte '\n')
        output.Flush()
        lock gate (fun () -> potentiallyChanged <- false)

    member _.Interrupt() =
        lock gate (fun () ->
            interrupted <- true
            if potentiallyChanged then 4 else 130)

    member _.Failure(error: exn) =
        let uncertain, sent = lock gate (fun () -> potentiallyChanged, dispatched)

        let reply =
            if uncertain then
                let reply = CliReply.unknown (CliReply.local ClientFailure.Unavailable)

                match pending with
                | Some frame -> CliReply.forAttempt frame AttemptKnowledge.Unconfirmed reply
                | None -> reply
            elif error :? OperationCanceledException then
                CliReply.local ClientFailure.Cancelled
            else
                CliReply.local ClientFailure.Unexpected

        let reply =
            match pending with
            | Some frame when not uncertain ->
                CliReply.forAttempt
                    frame
                    (Delivery.knowledge
                        (Frames.request frame |> WireRequest.endpoint)
                        (if sent then
                             AttemptResult.DispatchedWithoutReply
                         else
                             AttemptResult.NotDispatched))
                    reply
            | _ -> reply

        try
            errors.Write reply.Bytes
            errors.WriteByte(byte '\n')
            errors.Flush()
        with _ ->
            ()

        reply.ExitCode
