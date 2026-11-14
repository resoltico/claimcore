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
                                                                                                          Subject = DocumentSubject.Review(confirmation,
                                                                                                                                           consequence)
                                                                                                      }
                                                                                       }
                                             }) when not draft.PreviewOnly ->
                let consent =
                    match consequence with
                    | Consequence.Irreversible ->
                        let target =
                            match confirmation with
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

    let private allActions (document: HumanDocument) =
        document.Actions
        @ (document.Screen.Regions
           |> List.collect (fun region ->
               region.Actions
               @ (match region.Content with
                  | RegionContent.Table table -> table.Rows |> List.collect _.Actions
                  | RegionContent.Stages stages -> stages.Steps |> List.collect _.Actions
                  | RegionContent.Timeline entries -> entries |> List.collect _.Actions
                  | _ -> [])))

    let private selected (draft: InteractiveDraft) (action: Affordance) =
        let sameSelection =
            match draft.Input.Binding, action.Binding with
            | OperationBinding.Case(_, _, Some expected), OperationBinding.Case(_, _, Some actual) ->
                expected = actual
            | OperationBinding.Note(_, Some expected), OperationBinding.Note(_, Some actual) ->
                expected = actual
            | OperationBinding.Reminder(_, Some(expected, _)),
              OperationBinding.Reminder(_, Some(actual, _)) -> expected = actual
            | _ -> true

        let sameGrant =
            if draft.Input.Task <> ActKind.People PeopleActKind.Revoke then
                true
            else
                let normalize input value =
                    match input, value with
                    | TaskInputId.Target(InputTarget.Field BusinessField.CaseReference),
                      SubmissionValue.Text text ->
                        CaseReference.parse text
                        |> Result.map (
                            CaseReference.format >> CaseReference.key >> SubmissionValue.Text
                        )
                        |> Result.defaultValue value
                    | _ -> value

                action.Seeds
                |> Map.forall (fun input value ->
                    draft.Input.Inputs
                    |> Map.tryFind input
                    |> Option.exists (fun supplied ->
                        normalize input supplied = normalize input value))

        action.Task = TaskId.Operation draft.Input.Task && sameSelection && sameGrant

    let private complete newId (draft: InteractiveDraft) (action: Affordance) =
        let binding =
            match draft.Input.Task, draft.Input.Binding, action.Binding with
            | ActKind.Case CaseActKind.Open,
              OperationBinding.Case(target, _, _),
              OperationBinding.Case(_, revision, payment) ->
                OperationBinding.Case(target, revision, payment)
            | _ -> action.Binding

        let fixedInputs =
            action.Seeds
            |> Map.filter (fun input _ -> not (List.contains input action.Editable))

        let inputs =
            Map.fold
                (fun values input value -> Map.add input value values)
                draft.Input.Inputs
                fixedInputs

        OperationSubmission.admit
            {
                OperationId = draft.Input.OperationId |> Option.defaultWith newId
                Task = draft.Input.Task
                Binding = binding
                Inputs = inputs
            }
        |> Result.defaultWith (fun () ->
            invalidOp "A supplied affordance and acquired values form a declared submission.")

    let private unavailable console (draft: InteractiveDraft) =
        MessageCatalogs.renderCli draft.Language CliMessage.NotRecorded
        |> fun text -> console.Write(Encoding.UTF8.GetBytes(text + "\n"))

        ExitCode.value ExitCode.Rejected

    let private reviewDocument newId console transport draft installation document token =
        match allActions document |> List.tryFind (selected draft) with
        | Some action when action.Blocked.IsNone ->
            review console transport draft (complete newId draft action) installation token
        | _ -> Task.FromResult(unavailable console draft)

    let rec private readPages
        newId
        console
        transport
        draft
        installation
        endpoint
        input
        withCursor
        token
        =
        task {
            let frame =
                invocation endpoint input |> Frames.withRepresentation Representation.Human

            let! response = transport.Exchange frame token

            match unpack endpoint response with
            | Some(RepresentationReply.Human {
                                                 Outcome = HumanReadOutcome.Succeeded document
                                             }) ->
                if allActions document |> List.exists (selected draft) then
                    return! reviewDocument newId console transport draft installation document token
                else
                    match document.NextCursor with
                    | Some cursor ->
                        return!
                            readPages
                                newId
                                console
                                transport
                                draft
                                installation
                                endpoint
                                (withCursor input cursor)
                                withCursor
                                token
                    | None -> return unavailable console draft
            | _ ->
                transport.Deliver frame response
                return response.ExitCode
        }

    let private readTarget newId console transport (draft: InteractiveDraft) installation token =
        match draft.Input.Binding with
        | OperationBinding.People(Some actor) ->
            readPages
                newId
                console
                transport
                draft
                installation
                Api.peopleGrants
                {
                    ActorId = actor
                    AfterCursor = None
                    Limit = Paging.DefaultPageSize
                }
                (fun request cursor ->
                    { request with
                        GrantsRequest.AfterCursor = Some cursor
                    })
                token
        | OperationBinding.Case(target, _, Some _) ->
            readPages
                newId
                console
                transport
                draft
                installation
                Api.casePayments
                {
                    Target = target
                    AfterCursor = None
                    Limit = Paging.DefaultPageSize
                }
                (fun request cursor ->
                    { request with
                        CasePaymentsRequest.AfterCursor = Some cursor
                    })
                token
        | OperationBinding.Note(target, Some _) ->
            readPages
                newId
                console
                transport
                draft
                installation
                Api.caseNotes
                {
                    Target = target
                    AfterCursor = None
                    Limit = Paging.DefaultPageSize
                }
                (fun request cursor ->
                    { request with
                        CaseNotesRequest.AfterCursor = Some cursor
                    })
                token
        | OperationBinding.Reminder(target, Some(id, _)) ->
            readPages
                newId
                console
                transport
                draft
                installation
                Api.reminder
                { Target = target; ReminderId = id }
                (fun request _ -> request)
                token
        | OperationBinding.Case(target, _, _)
        | OperationBinding.Work(target, _)
        | OperationBinding.Note(target, _)
        | OperationBinding.Reminder(target, _) ->
            readPages
                newId
                console
                transport
                draft
                installation
                Api.caseGet
                { Target = target }
                (fun request _ -> request)
                token
        | OperationBinding.People None -> Task.FromResult(unavailable console draft)

    let private runValidated newId console transport (draft: InteractiveDraft) token =
        task {
            let frame =
                invocation Api.context () |> Frames.withRepresentation Representation.Human

            let! response = transport.Exchange frame token

            match unpack Api.context response with
            | Some(RepresentationReply.Human {
                                                 Outcome = HumanReadOutcome.Succeeded document
                                             }) ->
                match document.Subject with
                | DocumentSubject.Requester {
                                                State = RequesterKind.Active
                                                InstallationId = Some installation
                                            } ->
                    match draft.Input.Binding with
                    | OperationBinding.People None
                    | OperationBinding.Case(_, Some 0L, _) ->
                        return!
                            reviewDocument newId console transport draft installation document token
                    | _ -> return! readTarget newId console transport draft installation token
                | _ ->
                    transport.Deliver frame response
                    return ExitCode.value ExitCode.Rejected
            | _ ->
                transport.Deliver frame response
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
