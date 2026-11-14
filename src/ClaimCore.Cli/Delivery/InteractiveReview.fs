namespace ClaimCore.Cli

open ClaimCore.CommandLine

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

[<NoEquality; NoComparison>]
type internal InteractiveTransport =
    {
        Exchange: Invocation -> CancellationToken -> Task<CliReply>
        Deliver: Invocation -> CliReply -> unit
    }

/// A draft retains authored tokens and only metadata already supplied by the caller.
[<NoEquality; NoComparison>]
type internal InteractiveDraft =
    private
        {
            Input: SubmissionEnvelope<int64 option, Guid option>
            DisplayLanguage: string
            PreviewOnly: bool
        }

    member this.Language = this.DisplayLanguage

module internal InteractiveReview =
    let private hasOption name arguments =
        arguments
        |> List.exists (fun (value: string) ->
            value = name || value.StartsWith(name + "=", StringComparison.Ordinal))

    let private requestedText arguments =
        let separate =
            arguments
            |> List.pairwise
            |> List.choose (fun (name, value) -> if name = "--format" then Some value else None)

        let assigned =
            arguments
            |> List.choose (fun (value: string) ->
                if value.StartsWith("--format=", StringComparison.Ordinal) then
                    Some(value.Substring(9))
                else
                    None)

        match separate @ assigned with
        | []
        | [ "text" ] -> true
        | _ -> false

    let private operation arguments =
        (TaskId.all |> List.filter (TaskRouting.act >> Option.isSome))
        |> List.tryFind (fun entry ->
            let words = CommandGrammar.words entry

            words = List.truncate words.Length arguments
            && match (TaskRouting.act entry) with
               | Some(ActKind.Case _) -> true
               | Some(ActKind.Note _)
               | Some(ActKind.Work _)
               | Some(ActKind.Reminder _)
               | Some(ActKind.People _) -> true
               | _ -> false)

    let prepare console arguments =
        if
            not console.IsInteractive
            || not (requestedText arguments)
            || hasOption "--expected-revision" arguments
            || hasOption "--expected-work-revision" arguments
            || hasOption "--expected-reminder-revision" arguments
            || hasOption "--dry-run" arguments
            || hasOption "--confirm" arguments
        then
            Ok None
        else
            match operation arguments with
            | None -> Ok None
            | Some _ ->
                Grammar.draft arguments
                |> Result.map (fun (input, language, preview) ->
                    Some
                        {
                            Input = input
                            DisplayLanguage = language
                            PreviewOnly = preview
                        })

    let private invocation (endpoint: Endpoint<'Input, 'Output>) input =
        Frames.create endpoint input
        |> Result.defaultWith (fun _ ->
            invalidOp "A typed review step must produce an admitted invocation.")

    let private unpack (endpoint: Endpoint<'Input, 'Output>) (reply: CliReply) =
        match reply.Frame.Result with
        | FrameResult.Reply value when value.Route.Id = endpoint.Id ->
            ServiceReply.value endpoint value
        | FrameResult.Reply _ -> invalidOp "A review response must retain its requested endpoint."
        | _ -> None

    let private review
        (console: InteractiveConsole)
        transport
        (draft: InteractiveDraft)
        (input: OperationSubmission)
        installation
        token
        =
        task {
            let preview =
                invocation Api.operationPreview input
                |> Frames.withRepresentation Representation.Human

            let! response = transport.Exchange preview token
            transport.Deliver preview response

            match unpack Api.operationPreview response with
            | Some(RepresentationReply.Human {
                                                 Outcome = HumanPreviewResult.Reviewed {
                                                                                           Document = {
                                                                                                          Context = InteractionContext.Review binding
                                                                                                      }
                                                                                       }
                                             }) when not draft.PreviewOnly ->
                let consent =
                    match binding.Consequence with
                    | Consequence.Irreversible ->
                        let target =
                            match binding.Confirmation with
                            | ConfirmationTarget.CaseReference reference ->
                                CaseReference.format reference
                            | ConfirmationTarget.Operation id -> id.ToString("D")

                        CliConfirmation.exactTarget console draft.Language target
                    | _ ->
                        MessageCatalogs.renderCli draft.Language CliMessage.ReviewConfirmation
                        |> Encoding.UTF8.GetBytes
                        |> console.Write

                        CliConfirmation.confirmed draft.Language (console.ReadLine())

                if consent then
                    let execute =
                        invocation Api.operationExecute input
                        |> Frames.withRepresentation Representation.Human
                        |> (fun frame ->
                            if draft.Input.OperationId.IsNone then
                                Frames.provisional frame
                            else
                                frame)
                        |> Frames.bindInstallation installation
                        |> Result.defaultWith (fun _ ->
                            invalidOp "Reviewed scope must bind the request.")

                    let! accepted = transport.Exchange execute token
                    transport.Deliver execute accepted
                    do! WorkRefresh.run transport.Exchange transport.Deliver accepted token
                    return accepted.ExitCode
                else
                    MessageCatalogs.renderCli draft.Language CliMessage.NotRecorded
                    |> fun text -> Encoding.UTF8.GetBytes(text + "\n")
                    |> console.Write

                    return ExitCode.value ExitCode.Success
            | _ -> return response.ExitCode
        }

    let private complete newId (draft: InteractiveDraft) binding =
        let input =
            { draft.Input with
                OperationId = Some(draft.Input.OperationId |> Option.defaultWith newId)
                Binding = binding
            }

        OperationSubmission.completeDraft input
        |> Option.defaultWith (fun () ->
            invalidOp "Current review metadata completes the acquired submission.")

    let rec private runValidated newId console transport (draft: InteractiveDraft) token =
        task {
            let context =
                invocation Api.context () |> Frames.withRepresentation Representation.Human

            let! response = transport.Exchange context token

            match unpack Api.context response with
            | Some(RepresentationReply.Human {
                                                 Outcome = HumanReadOutcome.Succeeded {
                                                                                          Context = InteractionContext.Requester {
                                                                                                                                     State = RequesterKind.Active
                                                                                                                                     InstallationId = Some installation
                                                                                                                                 }
                                                                                      }
                                             }) ->
                match draft.Input.Binding with
                | OperationBinding.People _
                | OperationBinding.Case(_, Some 0L, _) ->
                    return!
                        review
                            console
                            transport
                            draft
                            (complete newId draft draft.Input.Binding)
                            installation
                            token
                | OperationBinding.Case(target, _, _)
                | OperationBinding.Work(target, _)
                | OperationBinding.Note(target, _)
                | OperationBinding.Reminder(target, _) ->
                    return! readCase newId console transport draft target installation token
            | Some(RepresentationReply.Human {
                                                 Outcome = HumanReadOutcome.Succeeded _
                                             }) ->
                transport.Deliver context response
                return ExitCode.value ExitCode.Rejected
            | _ ->
                transport.Deliver context response
                return response.ExitCode
        }

    and private readCase
        newId
        console
        transport
        (draft: InteractiveDraft)
        target
        installation
        token
        =
        task {
            let lookup =
                invocation Api.caseGet { CaseGetRequest.Target = target }
                |> Frames.withRepresentation Representation.Human

            let! response = transport.Exchange lookup token

            match unpack Api.caseGet response with
            | Some(RepresentationReply.Human {
                                                 Outcome = HumanReadOutcome.Succeeded {
                                                                                          Context = InteractionContext.Case current
                                                                                      }
                                             }) ->
                let target = CaseSelector.Id current.CaseId

                let submit binding =
                    review console transport draft (complete newId draft binding) installation token

                match draft.Input.Binding with
                | OperationBinding.Case(_, revision, payment) ->
                    return!
                        submit (
                            OperationBinding.Case(
                                target,
                                Some(defaultArg revision (Revision.value current.Revision)),
                                payment
                            )
                        )
                | OperationBinding.Work(_, revision) ->
                    return!
                        submit (
                            OperationBinding.Work(
                                target,
                                Some(defaultArg revision (Revision.value current.WorkRevision))
                            )
                        )
                | OperationBinding.Note(_, selected) ->
                    return! submit (OperationBinding.Note(target, selected))
                | OperationBinding.Reminder(_, None) ->
                    return! submit (OperationBinding.Reminder(target, None))
                | OperationBinding.Reminder(_, Some(id, Some revision)) ->
                    return! submit (OperationBinding.Reminder(target, Some(id, Some revision)))
                | OperationBinding.Reminder(_, Some(id, None)) ->
                    let frame =
                        invocation
                            Api.reminder
                            {
                                ReminderGetRequest.Target = target
                                ReminderId = id
                            }
                        |> Frames.withRepresentation Representation.Human

                    let! response = transport.Exchange frame token

                    match unpack Api.reminder response with
                    | Some(RepresentationReply.Human {
                                                         Outcome = HumanReadOutcome.Succeeded {
                                                                                                  Context = InteractionContext.Reminder current
                                                                                              }
                                                     }) ->
                        return!
                            submit (
                                OperationBinding.Reminder(
                                    target,
                                    Some(id, Some(Revision.value current.Revision))
                                )
                            )
                    | _ ->
                        transport.Deliver frame response
                        return response.ExitCode
                | OperationBinding.People _ -> return invalidOp "People drafts have no parent case."
            | _ ->
                transport.Deliver lookup response
                return response.ExitCode
        }

    let run newId console transport (draft: InteractiveDraft) token =
        let issues =
            match OperationSubmission.syntaxDraft draft.Input with
            | Ok issues -> issues
            | Error(SubmissionProblem.Rejected(Refusal.InvalidInput issues)) -> Some issues
            | Error _ -> invalidOp "Declared argv must assemble a valid submission shape."

        match issues with
        | None -> runValidated newId console transport draft token
        | Some issues ->
            DiagnosticText.refusalLines draft.Language (Refusal.InvalidInput issues)
            |> String.concat "\n"
            |> fun text -> Encoding.UTF8.GetBytes(text + "\n") |> console.Write

            Task.FromResult(ExitCode.value ExitCode.Rejected)
