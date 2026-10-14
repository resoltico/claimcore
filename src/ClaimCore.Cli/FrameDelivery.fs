namespace ClaimCore.Cli

open System
open System.IO
open ClaimCore.Contracts

/// A successful flush clears the current frame's dispatch knowledge.
type FrameDelivery(output: Stream, errors: Stream) =
    let gate = obj ()
    let mutable interrupted = false
    let mutable potentiallyChanged = false

    let advance action =
        lock gate (fun () ->
            if interrupted then
                raise (OperationCanceledException())

            action ())

    member _.BeginFrame() =
        advance (fun () -> potentiallyChanged <- false)

    member _.BeforeDispatch frame =
        advance (fun () -> potentiallyChanged <- (Frames.endpoint frame).Mutates)

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
        let uncertain = lock gate (fun () -> potentiallyChanged)

        let reply =
            if uncertain then
                CliReply.unknown (CliReply.local ClientFailure.Unavailable)
            elif error :? OperationCanceledException then
                CliReply.local ClientFailure.Cancelled
            else
                CliReply.local ClientFailure.Unexpected

        try
            errors.Write reply.Bytes
            errors.WriteByte(byte '\n')
            errors.Flush()
        with _ ->
            ()

        reply.ExitCode
