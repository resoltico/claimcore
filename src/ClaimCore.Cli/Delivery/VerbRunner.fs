namespace ClaimCore.Cli

open ClaimCore.CommandLine

open System
open System.IO
open System.Threading
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.Presentation

[<NoEquality; NoComparison>]
type internal VerbConsole =
    {
        Output: Stream
        Errors: Stream
        Review: InteractiveConsole
    }

/// Ordinary verbs and the shell share preparation, review, pagination and delivery.
module internal VerbRunner =
    let private render (prepared: PreparedVerb) frame (reply: CliReply) =
        if prepared.Format = VerbFormat.Json then
            reply.Bytes
        else
            ReplyText.bytesForWithDetails
                prepared.Details
                prepared.Rendering
                (Some frame)
                reply.Frame

    let private pages (io: VerbConsole) (processor: RemoteFrameProcessor) prepared token =
        let pageIO =
            {
                Exchange = fun frame token -> processor.Exchange(frame, token)
                Deliver = fun frame reply -> processor.Deliver(render prepared frame, reply)
                Output =
                    fun bytes ->
                        CliDiscovery.withOutput (fun () ->
                            io.Output.Write(bytes: byte array)
                            io.Output.Flush())
                Errors =
                    fun bytes ->
                        io.Errors.Write(bytes: byte array)
                        io.Errors.Flush()
            }

        PageCommand.run pageIO prepared token

    let private syntax invocation =
        [
            Frames.value Api.operationExecute invocation
            Frames.value Api.operationPreview invocation
        ]
        |> List.tryPick id
        |> Option.bind OperationSubmission.syntax

    let private syntaxReply invocation issues =
        let refusal = Refusal.InvalidInput issues

        let output (endpoint: Endpoint<'input, 'output>) (reply: 'output) =
            {
                ServiceReply.Route = Frames.endpoint invocation
                Wire = endpoint.Output.Write reply
                Typed = EndpointValues.output endpoint reply
            }

        let value =
            match Frames.value Api.operationExecute invocation with
            | Some input ->
                let core =
                    {
                        ExecuteReply.OperationId = input.OperationId
                        Outcome = ExecuteResult.Rejected refusal
                    }

                let reply =
                    match Frames.representation invocation with
                    | Representation.Human -> RepresentationReply.Human(HumanReplies.execute core)
                    | Representation.Data -> RepresentationReply.Data core

                output Api.operationExecute reply
            | None ->
                let input = Frames.value Api.operationPreview invocation |> Option.get

                let core =
                    {
                        PreviewReply.OperationId = input.OperationId
                        Outcome = Outcome.Rejected refusal
                    }

                let reply =
                    match Frames.representation invocation with
                    | Representation.Human ->
                        RepresentationReply.Human(HumanReplies.previewSubmission input core)
                    | Representation.Data -> RepresentationReply.Data core

                output Api.operationPreview reply

        let frame =
            {
                FrameReply.Endpoint = Some value.Endpoint
                OperationId = Frames.operationId invocation
                Delivery = DeliveryState.NotSent
                Result = FrameResult.Reply value
            }

        {
            CliReply.Frame = frame
            Bytes = Frames.encode frame
            ExitCode = Frames.exitCode frame
        }

    let private confirmationReference (processor: RemoteFrameProcessor) prepared token =
        task {
            let input = Frames.value Api.operationExecute prepared.Invocation |> Option.get

            let preview =
                Frames.create Api.operationPreview input
                |> Result.defaultWith (fun _ -> invalidOp "A prepared operation must preview.")
                |> Frames.withRepresentation Representation.Human

            let! dispatched, reply = processor.ExchangePrepared(preview, token)
            processor.Deliver(render prepared dispatched, reply)

            match reply.Frame.Result with
            | FrameResult.Reply value ->
                match ServiceReply.value Api.operationPreview value with
                | Some(RepresentationReply.Human {
                                                     Outcome = HumanPreviewResult.Reviewed review
                                                 }) ->
                    match review.Document.Subject with
                    | DocumentSubject.Review(ConfirmationTarget.CaseReference reference,
                                             Consequence.Irreversible) ->
                        match Frames.installationId dispatched with
                        | Some installation ->
                            let frozen =
                                Frames.bindInstallation installation prepared.Invocation
                                |> Result.defaultWith (fun _ ->
                                    invalidOp "Preview scope must bind its identical execution.")
                                |> Frames.freeze

                            return Ok(CaseReference.format reference, frozen)
                        | None -> return Error(ExitCode.value ExitCode.Failed)
                    | _ -> return Error(ExitCode.value ExitCode.Failed)
                | _ -> return Error reply.ExitCode
            | _ -> return Error reply.ExitCode
        }

    let rec private prepared (io: VerbConsole) (processor: RemoteFrameProcessor) arguments token =
        task {
            match
                Grammar.prepare (fun () -> Guid.CreateVersion7().ToString("D")) arguments
                |> Result.map (fun prepared -> prepared, syntax prepared.Invocation)
            with
            | Error issues ->
                CliDiscovery.writeText
                    io.Errors
                    (UsageText.render (CliInvocations.usageLanguage arguments) arguments issues)

                return ExitCode.value ExitCode.Usage
            | Ok(prepared, Some issues) ->
                let frame =
                    if prepared.Format = VerbFormat.Text then
                        Frames.withRepresentation Representation.Human prepared.Invocation
                    else
                        prepared.Invocation

                let reply = syntaxReply frame issues
                processor.Deliver(render prepared frame, reply)
                return reply.ExitCode
            | Ok(prepared, None) when prepared.DryRun ->
                CliDiscovery.writeOutputBytes io.Output prepared.Bytes
                return ExitCode.value ExitCode.Success
            | Ok(prepared, None) when
                CliConfirmation.validate io.Review.IsInteractive prepared |> Option.isSome
                ->
                let issue = CliConfirmation.validate io.Review.IsInteractive prepared |> Option.get

                CliDiscovery.writeText
                    io.Errors
                    (UsageText.render prepared.Language arguments [ issue ])

                return ExitCode.value ExitCode.Usage
            | Ok(prepared, None) when prepared.All || prepared.Format = VerbFormat.Csv ->
                return! pages io processor prepared token
            | Ok(prepared, None) when CliConfirmation.needsReferencePreview prepared.Invocation ->
                match! confirmationReference processor prepared token with
                | Error exit -> return exit
                | Ok(reference, frozen) ->
                    let issue =
                        match prepared.Confirmation with
                        | Some supplied when supplied <> reference ->
                            Some(UsageError.InvalidChoice("--confirm", supplied, [ reference ]))
                        | None when not io.Review.IsInteractive ->
                            Some(UsageError.MissingOption "--confirm")
                        | _ -> None

                    match issue with
                    | Some issue ->
                        CliDiscovery.writeText
                            io.Errors
                            (UsageText.render prepared.Language arguments [ issue ])

                        return ExitCode.value ExitCode.Usage
                    | None ->
                        if
                            prepared.Confirmation.IsSome
                            || CliConfirmation.exactTarget io.Review prepared.Language reference
                        then
                            return! execute processor prepared frozen token
                        else
                            CliDiscovery.writeText
                                io.Output
                                (MessageCatalogs.renderCli prepared.Language CliMessage.NotRecorded)

                            return ExitCode.value ExitCode.Success
            | Ok(prepared, None) ->
                let frame = prepared.Invocation

                let consent =
                    match CliConfirmation.irreversibleTarget frame, prepared.Confirmation with
                    | Some target, None ->
                        CliConfirmation.exactTarget io.Review prepared.Language target
                    | _ -> true

                if not consent then
                    CliDiscovery.writeText
                        io.Output
                        (MessageCatalogs.renderCli prepared.Language CliMessage.NotRecorded)

                    return ExitCode.value ExitCode.Success
                else
                    return! execute processor prepared frame token
        }

    and private execute (processor: RemoteFrameProcessor) prepared frame token =
        task {
            let frame =
                if prepared.Format = VerbFormat.Text then
                    Frames.withRepresentation Representation.Human frame
                else
                    frame

            let! reply = processor.Exchange(frame, token)
            processor.Deliver(render prepared frame, reply)

            if prepared.Format = VerbFormat.Text then
                do!
                    WorkRefresh.run
                        (fun frame token -> processor.Exchange(frame, token))
                        (fun frame reply -> processor.Deliver(render prepared frame, reply))
                        reply
                        token

            return reply.ExitCode
        }

    let private offline (io: VerbConsole) arguments =
        match Grammar.prepareOffline arguments with
        | Error issues ->
            CliDiscovery.writeText
                io.Errors
                (UsageText.render (CliInvocations.usageLanguage arguments) arguments issues)

            Some(ExitCode.value ExitCode.Usage)
        | Ok None -> None
        | Ok(Some draft) ->
            match draft.Issues with
            | None ->
                CliDiscovery.writeOutputBytes io.Output draft.Bytes
                Some(ExitCode.value ExitCode.Success)
            | Some issues ->
                let refusal = Refusal.InvalidInput issues

                if draft.Format = VerbFormat.Json then
                    JsonValue.Object
                        [
                            "delivery", JsonValue.String "NOT_SENT"
                            "diagnostic", DiagnosticCodecs.diagnostic.Write refusal
                        ]
                    |> WireWriter.bytes
                    |> CliDiscovery.writeOutputBytes io.Output
                else
                    DiagnosticText.refusalLines draft.Rendering refusal
                    |> String.concat "\n"
                    |> fun text -> CliDiscovery.writeText io.Errors (text + "\n")

                Some(ExitCode.value ExitCode.Rejected)

    let run io (processor: RemoteFrameProcessor) arguments token =
        task {
            let expanded = TextInputs.expand io.Review.IsInteractive arguments

            match
                expanded
                |> Result.bind (fun arguments ->
                    InteractiveReview.prepare io.Review arguments
                    |> Result.map (fun draft -> arguments, draft))
            with
            | Error issues ->
                CliDiscovery.writeText
                    io.Errors
                    (UsageText.render (CliInvocations.usageLanguage arguments) arguments issues)

                return ExitCode.value ExitCode.Usage
            | Ok(arguments, None) ->
                match offline io arguments with
                | Some code -> return code
                | None -> return! prepared io processor arguments token
            | Ok(_, Some draft) ->
                let transport =
                    {
                        Exchange = fun frame token -> processor.Exchange(frame, token)
                        Deliver =
                            fun frame reply ->
                                processor.Deliver(
                                    (fun response ->
                                        ReplyText.bytesForWithDetails
                                            (VerbOptions.hasOption "--details" arguments)
                                            draft.Rendering
                                            (Some frame)
                                            response.Frame),
                                    reply
                                )
                    }

                return! InteractiveReview.run Guid.CreateVersion7 io.Review transport draft token
        }
