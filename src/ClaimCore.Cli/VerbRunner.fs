namespace ClaimCore.Cli

open System
open System.IO
open System.Threading
open ClaimCore.Application.Contracts
open ClaimCore.Domain
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
            TextReply.bytesFor prepared.Language (Some frame) reply.Frame

    let private pages (io: VerbConsole) (processor: RemoteFrameProcessor) prepared token =
        let pageIO =
            {
                Exchange = fun frame token -> processor.Exchange(frame, token)
                Deliver = fun frame reply -> processor.Deliver(render prepared frame, reply)
                Output =
                    fun bytes ->
                        io.Output.Write(bytes: byte array)
                        io.Output.Flush()
                Errors =
                    fun bytes ->
                        io.Errors.Write(bytes: byte array)
                        io.Errors.Flush()
            }

        let frame = prepared.Invocation

        if (Frames.endpoint frame).Id = Api.authorityHistory.Id then
            AuthorityHistoryCommand.run pageIO prepared token
        elif List.contains (Frames.endpoint frame).Id [ Api.reminders.Id; Api.caseTimeline.Id ] then
            WorkListCommand.run pageIO prepared token
        else
            CaseListCommand.run pageIO prepared token

    let private syntax invocation =
        [
            Frames.value Api.operationExecute invocation
            Frames.value Api.operationPreview invocation
        ]
        |> List.tryPick id
        |> Option.bind ClaimCore.Application.Contracts.OperationInput.syntax

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
                output
                    Api.operationExecute
                    {
                        ClaimCore.Application.Contracts.ExecuteReply.OperationId =
                            ClaimCore.Application.Contracts.OperationInput.operationId input
                        Outcome = ClaimCore.Application.Contracts.ExecuteResult.Rejected refusal
                    }
            | None ->
                let input = Frames.value Api.operationPreview invocation |> Option.get

                output
                    Api.operationPreview
                    {
                        ClaimCore.Presentation.PresentedPreviewReply.OperationId =
                            ClaimCore.Application.Contracts.OperationInput.operationId input
                        Outcome = ClaimCore.Application.Contracts.Outcome.Rejected refusal
                    }

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
                let reply = syntaxReply prepared.Invocation issues
                processor.Deliver(render prepared prepared.Invocation, reply)
                return reply.ExitCode
            | Ok(prepared, None) when prepared.DryRun ->
                CliDiscovery.writeBytes io.Output prepared.Bytes
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
            | Ok(arguments, None) -> return! prepared io processor arguments token
            | Ok(_, Some draft) ->
                let transport =
                    {
                        Exchange = fun frame token -> processor.Exchange(frame, token)
                        Deliver =
                            fun frame reply ->
                                processor.Deliver(
                                    (fun response ->
                                        TextReply.bytesFor
                                            draft.Language
                                            (Some frame)
                                            response.Frame),
                                    reply
                                )
                    }

                return! InteractiveReview.run Guid.CreateVersion7 io.Review transport draft token
        }
