namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Protocol

open System
open System.IO

/// The current request stays frozen until its complete reply is flushed.
type FrameDelivery(output: Stream, errors: Stream, language: string, render: CliReply -> byte array)
    =
    let gate = obj ()
    let mutable currentLanguage = language
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
                errors.Write(
                    System.Text.Encoding.UTF8.GetBytes(
                        (MessageCatalogs.renderCli
                            currentLanguage
                            (CliMessage.UnresolvedDelivery(
                                CliIdentity.programName + " call",
                                CliIdentity.programName
                                + " operation resolve "
                                + (Frames.operationId frame
                                   |> Option.map string
                                   |> Option.defaultValue "OPERATION_ID")
                            ))
                         + "\n")
                    )
                )

                errors.Write(Frames.encodeInvocation frame)
                errors.WriteByte(byte '\n')
                errors.Flush())

    let writeWith (renderReply: CliReply -> byte array) (reply: CliReply) =
        advance ignore
        output.Write(renderReply reply)
        output.WriteByte(byte '\n')
        output.Flush()

        if reply.ExitCode = ExitCode.value ExitCode.OutcomeUnknown then
            retryFrame ()

        lock gate (fun () ->
            potentiallyChanged <- reply.ExitCode = ExitCode.value ExitCode.OutcomeUnknown)

    new(output, errors, language) =
        new FrameDelivery(output, errors, language, (fun reply -> reply.Bytes))

    /// The shell calls this only after the preceding command has fully stopped.
    member internal _.BeginCommand(language) =
        lock gate (fun () ->
            interrupted <- false
            potentiallyChanged <- false
            dispatched <- false
            pending <- None
            currentLanguage <- language)

    member _.BeginFrame() =
        advance (fun () ->
            potentiallyChanged <- false
            dispatched <- false
            pending <- None)

    member _.Prepare frame =
        advance (fun () ->
            pending <- Some frame
            potentiallyChanged <- false)

    member _.BeforeDispatch frame =
        advance (fun () ->
            potentiallyChanged <- Frames.endpoint frame |> Api.executesLedger
            dispatched <- true
            pending <- Some frame)

    member _.Write(reply: CliReply) = writeWith render reply
    member internal _.WriteWith(renderReply, reply) = writeWith renderReply reply

    member _.Interrupt() =
        lock gate (fun () ->
            interrupted <- true

            if potentiallyChanged then
                try
                    retryFrame ()
                with _ ->
                    ()

                ExitCode.value ExitCode.OutcomeUnknown
            else
                ExitCode.value ExitCode.Cancelled)

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
