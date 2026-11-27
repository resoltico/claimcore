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

/// A local publication remains successful if an interrupt races with the completed write.
[<RequireQualifiedAccess>]
type internal VerbCompletion =
    | Completed of exitCode: int
    | OutputPublished

module internal VerbCompletion =
    let exitCode =
        function
        | VerbCompletion.Completed code -> code
        | VerbCompletion.OutputPublished -> ExitCode.value ExitCode.Success

/// Ordinary verbs and the shell share preparation, review, pagination and delivery.
module internal VerbRunner =
    let private render (prepared: PreparedVerb) frame (reply: CliReply) =
        if prepared.Format = VerbFormat.Json then
            reply.Bytes
        else
            ReplyText.bytesForReply prepared.Details prepared.Rendering (Some frame) reply

    let private readOutput (io: VerbConsole) (processor: RemoteFrameProcessor) prepared token =
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

    let private exportSelection frame =
        match (Frames.endpoint frame).Id with
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.CaseList) ->
            Frames.value Api.caseList frame
            |> Option.map (fun request -> ExportSelection.Cases request.Filter, request.Page)
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.CaseSummary) ->
            Frames.value Api.caseSummary frame
            |> Option.map (fun request ->
                ExportSelection.CaseSummary request.Selection, request.Page)
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.CaseTimeline) ->
            Frames.value Api.caseTimeline frame
            |> Option.map (fun request ->
                ExportSelection.Timeline request,
                {
                    Limit = request.Limit
                    AfterCursor = request.AfterCursor
                })
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.ReminderList) ->
            Frames.value Api.reminders frame
            |> Option.map (fun request ->
                ExportSelection.Reminders request,
                {
                    Limit = request.Limit
                    AfterCursor = request.AfterCursor
                })
        | EndpointId.Actor(ActorEndpoint.Query QueryTaskId.PeopleHistory) ->
            Frames.value Api.peopleHistory frame
            |> Option.map (fun request ->
                ExportSelection.PeopleHistory request,
                {
                    Limit = request.Limit
                    AfterCursor = request.AfterCursor
                })
        | _ -> None

    let private exportCsv
        (io: VerbConsole)
        (processor: RemoteFrameProcessor)
        (prepared: PreparedVerb)
        token
        =
        task {
            let frame = prepared.Invocation

            match exportSelection frame with
            | None -> return ExitCode.value ExitCode.Usage
            | Some(selection, page) ->
                let window =
                    if prepared.All || page.AfterCursor.IsSome then
                        Some
                            {
                                Limit = page.Limit
                                After = page.AfterCursor
                            }
                    else
                        None

                let mutable published = false

                let publish bytes =
                    CliDiscovery.writeOutputBytes io.Output bytes
                    published <- true

                let failure code lines =
                    if published then
                        CliDiscovery.writeText
                            io.Errors
                            (ReplyTextFormat.label prepared.Rendering "notice.incompleteExport")

                    lines |> List.iter (CliDiscovery.writeText io.Errors)
                    ExitCode.value code

                let advice value =
                    HumanText.advice prepared.Rendering value

                match! processor.Export(frame, selection, window, publish, token) with
                | Ok() -> return ExitCode.value ExitCode.Success
                | Error(ExportDownloadFailure.Service reply) ->
                    if published then
                        failure ExitCode.Failed [] |> ignore

                    CliDiscovery.writeOutputBytes
                        io.Errors
                        (ReplyText.bytesForReply
                            prepared.Details
                            prepared.Rendering
                            (Some frame)
                            reply)

                    return reply.ExitCode
                | Error(ExportDownloadFailure.Local error) ->
                    if published then
                        failure ExitCode.Failed [] |> ignore

                    let reply =
                        CliReply.local error |> CliReply.forAttempt frame DeliveryState.NotSent

                    CliDiscovery.writeOutputBytes
                        io.Errors
                        (ReplyText.bytesForReply
                            prepared.Details
                            prepared.Rendering
                            (Some frame)
                            reply)

                    return reply.ExitCode
                | Error(ExportDownloadFailure.Export error) ->
                    return
                        match error with
                        | ExportFailure.Rejected rejection ->
                            failure
                                ExitCode.Rejected
                                (HumanText.rejection prepared.Details prepared.Rendering rejection)
                        | ExportFailure.Failed fault ->
                            failure ExitCode.Failed (HumanText.fault prepared.Rendering fault)
                        | ExportFailure.LimitExceeded value
                        | ExportFailure.InvalidContinuation value
                        | ExportFailure.ProcessingTimeout value ->
                            failure ExitCode.Failed (advice value)
        }

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
            HostContractIdentity = None
        }

    let private confirmationReference (processor: RemoteFrameProcessor) prepared token =
        task {
            let input = Frames.value Api.operationExecute prepared.Invocation |> Option.get

            let preview =
                Frames.create Api.operationPreview input
                |> Result.defaultWith (fun _ -> invalidOp "A prepared operation must preview.")
                |> Frames.withRepresentation Representation.Human

            let! dispatched, reply = processor.ExchangePrepared(preview, token)

            let deliver () =
                processor.Deliver(render prepared dispatched, reply)

            if prepared.Format = VerbFormat.Text then
                deliver ()

            let refusal () =
                if prepared.Format <> VerbFormat.Text then
                    deliver ()

                Error reply.ExitCode

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
                | _ -> return refusal ()
            | _ -> return refusal ()
        }

    let private attachmentPreflightReply invocation (response: CliReply) =
        match response.Frame.Result with
        | FrameResult.Reply value ->
            let outcome =
                match ServiceReply.value Api.operationPreview value with
                | Some(RepresentationReply.Data {
                                                    Outcome = Outcome.Succeeded(Previewed.AlreadyAccepted receipt)
                                                }) -> ExecuteResult.AlreadyAccepted receipt
                | Some(RepresentationReply.Data { Outcome = Outcome.Rejected refusal }) ->
                    ExecuteResult.Rejected refusal
                | Some(RepresentationReply.Data { Outcome = Outcome.Failed fault }) ->
                    ExecuteResult.Failed fault
                | _ -> invalidOp "A machine preflight must return a terminal DATA outcome."

            let reply =
                RepresentationReply.Data
                    {
                        ExecuteReply.OperationId = Frames.operationId invocation |> Option.get
                        Outcome = outcome
                    }

            let delivery =
                match outcome with
                | ExecuteResult.AlreadyAccepted _ -> DeliveryState.Delivered
                | _ -> DeliveryState.NotSent

            CliReply.result invocation (Api.operationExecute.Output.Write reply)
            |> Result.defaultWith (fun _ ->
                invalidOp "Preflight must preserve the execution reply contract.")
            |> CliReply.forAttempt invocation delivery
        | _ -> CliReply.forAttempt invocation DeliveryState.NotSent response

    let private describeMachineReview
        (io: VerbConsole)
        (prepared: PreparedVerb)
        submission
        frame
        (reply: PreviewReply)
        =
        let humanFrame = Frames.withRepresentation Representation.Human frame

        let human =
            RepresentationReply.Human(HumanReplies.previewSubmission submission reply)

        let rendered =
            CliReply.result humanFrame (Api.operationPreview.Output.Write human)
            |> Result.defaultWith (fun _ -> invalidOp "A reviewed DATA result can render as text.")
            |> fun described ->
                ReplyText.bytesForWithDetails
                    prepared.Details
                    prepared.Rendering
                    (Some humanFrame)
                    described.Frame

        io.Errors.Write(rendered)
        io.Errors.WriteByte(byte '\n')
        io.Errors.Flush()

    let private attachmentReview
        (io: VerbConsole)
        (processor: RemoteFrameProcessor)
        prepared
        token
        =
        task {
            let submission = Frames.value Api.operationExecute prepared.Invocation |> Option.get
            let machine = prepared.Format = VerbFormat.Json

            let preview =
                Frames.create Api.operationPreview submission
                |> Result.defaultWith (fun _ ->
                    invalidOp "An admitted attachment operation can be reviewed.")
                |> Frames.withRepresentation (
                    if machine then
                        Representation.Data
                    else
                        Representation.Human
                )

            let! dispatched, reply = processor.ExchangePrepared(preview, token)

            if not machine then
                processor.Deliver(render prepared dispatched, reply)

            let result =
                match reply.Frame.Result with
                | FrameResult.Reply value -> ServiceReply.value Api.operationPreview value
                | _ -> None

            let reviewed =
                match result with
                | Some(RepresentationReply.Human {
                                                     Outcome = HumanPreviewResult.Reviewed _
                                                 }) -> true
                | Some(RepresentationReply.Data {
                                                    Outcome = Outcome.Succeeded(Previewed.Reviewed _)
                                                }) -> true
                | _ -> false

            if not reviewed then
                if machine then
                    let terminal = attachmentPreflightReply prepared.Invocation reply
                    processor.Deliver(render prepared prepared.Invocation, terminal)
                    return Error terminal.ExitCode
                else
                    return Error reply.ExitCode
            else
                let confirmed =
                    match prepared.Confirmation with
                    | Some value -> String.Equals(value, "yes", StringComparison.Ordinal)
                    | None when io.Review.IsInteractive ->
                        let prompt =
                            MessageCatalogs.renderCli
                                prepared.Language
                                CliMessage.ReviewConfirmation

                        match result with
                        | Some(RepresentationReply.Data review) ->
                            describeMachineReview io prepared submission dispatched review
                            CliDiscovery.writeText io.Errors prompt
                        | _ -> prompt |> System.Text.Encoding.UTF8.GetBytes |> io.Review.Write

                        CliConfirmation.confirmed prepared.Language (io.Review.ReadLine())
                    | _ -> false

                if not confirmed then
                    CliDiscovery.writeText
                        io.Errors
                        (MessageCatalogs.renderCli prepared.Language CliMessage.NotRecorded)

                    return Error(ExitCode.value ExitCode.Cancelled)
                else
                    return
                        Frames.installationId dispatched
                        |> Option.bind (fun installation ->
                            Frames.bindInstallation installation prepared.Invocation
                            |> Result.toOption)
                        |> Option.map Frames.freeze
                        |> Option.map Ok
                        |> Option.defaultValue (Error(ExitCode.value ExitCode.Failed))
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
            | Ok(prepared, None) when
                Frames.value Api.operationExecute prepared.Invocation
                |> Option.exists (fun input ->
                    CommandGrammar.attachmentTransfer (TaskId.token input.Task))
                ->
                match! attachmentReview io processor prepared token with
                | Ok frame -> return! execute processor prepared frame token
                | Error exit -> return exit
            | Ok(prepared, None) when prepared.Format = VerbFormat.Csv ->
                return! exportCsv io processor prepared token
            | Ok(prepared, None) when prepared.All -> return! readOutput io processor prepared token
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
                    match CliConfirmation.requiredTarget frame, prepared.Confirmation with
                    | Some target, None ->
                        CliConfirmation.exactConfirmation io.Review prepared.Language target
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

    let private runVerb io (processor: RemoteFrameProcessor) arguments token =
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
                                        ReplyText.bytesWithHostIdentity
                                            (VerbOptions.hasOption "--details" arguments)
                                            draft.Rendering
                                            (Some frame)
                                            response.Frame
                                            response.HostContractIdentity),
                                    reply
                                )
                    }

                return! InteractiveReview.run Guid.CreateVersion7 io.Review transport draft token
        }


    let private download
        saveAttachment
        io
        (processor: RemoteFrameProcessor)
        operation
        arguments
        token
        =
        task {
            match Grammar.prepareAttachmentDownload operation arguments with
            | Error problems ->
                CliDiscovery.writeText
                    io.Errors
                    (UsageText.render (CliInvocations.usageLanguage arguments) arguments problems)

                return VerbCompletion.Completed(ExitCode.value ExitCode.Usage)
            | Ok(selection, path, rendering) ->
                let write stream message =
                    CliDiscovery.writeText
                        stream
                        (MessageCatalogs.renderCli rendering.Language message + "\n")

                let errors lines =
                    lines
                    |> String.concat "\n"
                    |> fun text -> CliDiscovery.writeText io.Errors (text + "\n")

                match! processor.Download(selection, token) with
                | Ok bytes ->
                    match saveAttachment path bytes token with
                    | Ok() ->
                        write io.Output CliMessage.AttachmentSaved
                        return VerbCompletion.OutputPublished
                    | Error() ->
                        write io.Errors CliMessage.AttachmentOutputFailed
                        return VerbCompletion.Completed(ExitCode.value ExitCode.Failed)
                | Error(AttachmentDownloadFailure.Access reply) ->
                    errors (
                        ReplyText.resultLines rendering None reply.Frame.Result
                        @ ReplyText.contractIdentityLines rendering reply.HostContractIdentity
                    )

                    return VerbCompletion.Completed reply.ExitCode
                | Error(AttachmentDownloadFailure.Local reason) ->
                    let reply = CliReply.local reason

                    errors (
                        ReplyText.resultLines rendering None reply.Frame.Result
                        @ ReplyText.contractIdentityLines rendering reply.HostContractIdentity
                    )

                    return VerbCompletion.Completed reply.ExitCode
                | Error(AttachmentDownloadFailure.Host(failure, identity)) ->
                    errors (
                        ReplyText.resultLines rendering None (FrameResult.HostFailure failure)
                        @ ReplyText.contractIdentityLines rendering identity
                    )

                    return VerbCompletion.Completed(ExitCode.value ExitCode.Failed)
                | Error(AttachmentDownloadFailure.Refused(AttachmentTransferFailure.Rejected rejection)) ->
                    errors (HumanText.rejection false rendering rejection)
                    return VerbCompletion.Completed(ExitCode.value ExitCode.Rejected)
                | Error(AttachmentDownloadFailure.Refused(AttachmentTransferFailure.Failed fault)) ->
                    errors (DiagnosticText.faultLines rendering fault)
                    return VerbCompletion.Completed(ExitCode.value ExitCode.Failed)
        }

    let runWithCompletion saveAttachment io processor arguments token =
        match CommandGrammar.tryBinaryRead arguments with
        | Some operation -> download saveAttachment io processor operation arguments token
        | None ->
            task {
                let! code = runVerb io processor arguments token
                return VerbCompletion.Completed code
            }

    let run io processor arguments token =
        task {
            let! completion = runWithCompletion AttachmentFiles.save io processor arguments token
            return VerbCompletion.exitCode completion
        }
