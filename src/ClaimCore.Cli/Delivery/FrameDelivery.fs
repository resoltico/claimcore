namespace ClaimCore.Cli

open ClaimCore.CommandLine

open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

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
            | Some frame when Api.terminatesSessions (Frames.endpoint frame) ->
                let message =
                    MessageCatalogs.renderCli currentLanguage CliMessage.OperationalUnresolved

                errors.Write(System.Text.Encoding.UTF8.GetBytes(message + "\n"))

                if (Frames.installationId frame).IsSome then
                    let bytes = Frames.terminationRecovery frame |> Frames.encodeInvocation
                    errors.Write bytes
                    errors.WriteByte(byte '\n')
            | Some frame when Api.effect (Frames.endpoint frame) = EndpointEffect.OperationalWrite ->
                let message =
                    MessageCatalogs.renderCli currentLanguage CliMessage.OperationalUnresolved

                errors.Write(System.Text.Encoding.UTF8.GetBytes(message + "\n"))
            | Some frame ->
                let status =
                    CommandGrammar.commandText (QueryTaskId.key QueryTaskId.OperationGet)
                    + " "
                    + (Frames.operationId frame
                       |> Option.map string
                       |> Option.defaultValue "OPERATION_ID")

                match Frames.upload frame with
                | Some _ ->
                    let message =
                        MessageCatalogs.renderCli
                            currentLanguage
                            (CliMessage.AttachmentUnresolved status)

                    errors.Write(System.Text.Encoding.UTF8.GetBytes(message + "\n"))
                | None ->
                    let message =
                        MessageCatalogs.renderCli
                            currentLanguage
                            (CliMessage.UnresolvedDelivery(
                                CliIdentity.programName + " call",
                                status
                            ))

                    errors.Write(System.Text.Encoding.UTF8.GetBytes(message + "\n"))
                    errors.Write(Frames.encodeInvocation frame)
                    errors.WriteByte(byte '\n')

                errors.Flush())

    let writeWith (renderReply: CliReply -> byte array) (reply: CliReply) =
        advance ignore

        CliDiscovery.withOutput (fun () ->
            output.Write(renderReply reply)
            output.WriteByte(byte '\n')
            output.Flush())

        let uncertainChange =
            reply.ExitCode = ExitCode.value ExitCode.OutcomeUnknown
            && Frames.mayChange reply.Frame

        if uncertainChange then
            retryFrame ()

        lock gate (fun () ->
            potentiallyChanged <- uncertainChange

            if pending |> Option.exists (Frames.upload >> Option.isSome) then
                pending <- None)

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
            potentiallyChanged <- Api.effect (Frames.endpoint frame) <> EndpointEffect.Read

            if Frames.reportsIdentity frame && not dispatched then
                Frames.operationId frame
                |> Option.iter (fun operation ->
                    let bytes = System.Text.Encoding.UTF8.GetBytes(operation.ToString("D") + "\n")
                    errors.Write bytes
                    errors.Flush())

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
        let uncertain, sent, isRead =
            lock gate (fun () ->
                potentiallyChanged,
                dispatched,
                (pending
                 |> Option.exists (fun frame ->
                     Api.effect (Frames.endpoint frame) = EndpointEffect.Read)))

        match error with
        | CliDiscovery.OutputPipeClosed _ when not uncertain && isRead ->
            ExitCode.value ExitCode.Success
        | _ ->
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
