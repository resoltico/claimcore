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

[<RequireQualifiedAccess>]
type internal DraftAct =
    | Case of AuthoredCaseAct
    | Note of AuthoredNoteAct
    | Work of AuthoredWorkAct
    | Reminder of ReminderAct<int64 option>

[<RequireQualifiedAccess>]
type internal DraftIntent =
    | Case of reference: string * act: DraftAct
    | People of target: PrincipalInput * act: AuthoredPeopleAct

/// A structurally checked draft has no executable revision or automatically allocated operation id.
[<NoEquality; NoComparison>]
type internal InteractiveDraft =
    private
        {
            Intent: DraftIntent
            SuppliedId: Guid option
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
            | Some entry ->
                let caseIntent adapt result =
                    result
                    |> Result.map (fun ((target, act, supplied), language, preview) ->
                        let reference =
                            match target with
                            | CaseSelector.Reference reference -> reference
                            | CaseSelector.Id _ ->
                                invalidOp "Interactive case bindings require a reference."

                        DraftIntent.Case(reference, adapt act), supplied, language, preview)

                let intent =
                    match (TaskRouting.act entry) with
                    | Some(ActKind.People _) ->
                        Grammar.peopleIntent arguments
                        |> Result.map (fun ((target, act, supplied), language, preview) ->
                            DraftIntent.People(target, act), supplied, language, preview)
                    | Some(ActKind.Note _) ->
                        Grammar.noteIntent arguments |> caseIntent DraftAct.Note
                    | Some(ActKind.Reminder _) ->
                        Grammar.reminderIntent arguments |> caseIntent DraftAct.Reminder
                    | Some(ActKind.Work _) ->
                        Grammar.workIntent arguments |> caseIntent DraftAct.Work
                    | _ -> Grammar.caseIntent arguments |> caseIntent DraftAct.Case

                intent
                |> Result.map (fun (intent, supplied, language, preview) ->
                    Some
                        {
                            Intent = intent
                            SuppliedId = supplied
                            DisplayLanguage = language
                            PreviewOnly = preview
                        })

    let private authoredIntent (draft: InteractiveDraft) =
        match draft.Intent with
        | DraftIntent.Case(reference, DraftAct.Case act) ->
            AuthoredIntent.Case(draft.SuppliedId, Some reference, None, act)
        | DraftIntent.Case(reference, DraftAct.Note act) ->
            AuthoredIntent.Note(draft.SuppliedId, Some reference, act)
        | DraftIntent.Case(reference, DraftAct.Work(WorkAct.UpdateWork work)) ->
            AuthoredIntent.Work(draft.SuppliedId, Some reference, None, work)
        | DraftIntent.Case(reference, DraftAct.Reminder act) ->
            AuthoredIntent.Reminder(draft.SuppliedId, Some reference, act)
        | DraftIntent.People(target, act) -> AuthoredIntent.People(draft.SuppliedId, target, act)

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
        (input: OperationInput)
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
                            if draft.SuppliedId.IsNone then
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

    let private reminderIdentity =
        function
        | ReminderAct.Create _ -> None
        | ReminderAct.Update(id, _, _)
        | ReminderAct.Complete(id, _)
        | ReminderAct.Cancel(id, _)
        | ReminderAct.Reopen(id, _, _)
        | ReminderAct.CompleteAndPlanNext(id, _, _) -> Some id

    let private reminderAct revision =
        function
        | ReminderAct.Create plan -> ReminderAct.Create plan
        | ReminderAct.Update(id, expected, plan) ->
            ReminderAct.Update(id, defaultArg expected revision, plan)
        | ReminderAct.Complete(id, expected) ->
            ReminderAct.Complete(id, defaultArg expected revision)
        | ReminderAct.Cancel(id, expected) -> ReminderAct.Cancel(id, defaultArg expected revision)
        | ReminderAct.Reopen(id, expected, due) ->
            ReminderAct.Reopen(id, defaultArg expected revision, due)
        | ReminderAct.CompleteAndPlanNext(id, expected, plan) ->
            ReminderAct.CompleteAndPlanNext(id, defaultArg expected revision, plan)

    let rec private runValidated newId console transport (draft: InteractiveDraft) token =
        task {
            let context =
                invocation Api.context () |> Frames.withRepresentation Representation.Human

            let! contextReply = transport.Exchange context token

            match unpack Api.context contextReply with
            | Some(RepresentationReply.Human {
                                                 Outcome = HumanReadOutcome.Succeeded {
                                                                                          Context = InteractionContext.Requester {
                                                                                                                                     State = RequesterKind.Active
                                                                                                                                     InstallationId = Some installation
                                                                                                                                 }
                                                                                      }
                                             }) ->
                match draft.Intent with
                | DraftIntent.Case(reference, DraftAct.Case act) when
                    (CaseActKind.describe (CaseAct.kind act)).Revision = RevisionInput.New
                    ->
                    let input =
                        OperationInput.Case
                            {
                                OperationId = draft.SuppliedId |> Option.defaultWith newId
                                Target = CaseSelector.Reference reference
                                ExpectedRevision = 0L
                                Act = act
                            }

                    return! review console transport draft input installation token
                | DraftIntent.Case(reference, act) ->
                    return! readCase newId console transport draft reference act installation token
                | DraftIntent.People(target, act) ->
                    let input =
                        OperationInput.People
                            {
                                OperationId = draft.SuppliedId |> Option.defaultWith newId
                                Target = target
                                Act = act
                            }

                    return! review console transport draft input installation token
            | Some(RepresentationReply.Human {
                                                 Outcome = HumanReadOutcome.Succeeded _
                                             }) ->
                transport.Deliver context contextReply
                return ExitCode.value ExitCode.Rejected
            | _ ->
                transport.Deliver context contextReply
                return contextReply.ExitCode
        }

    and private readCase
        newId
        console
        transport
        (draft: InteractiveDraft)
        reference
        act
        installation
        token
        =
        task {
            let lookup =
                invocation
                    Api.caseGet
                    {
                        CaseGetRequest.Target = CaseSelector.Reference reference
                    }

            let lookup = Frames.withRepresentation Representation.Human lookup
            let! response = transport.Exchange lookup token

            match unpack Api.caseGet response with
            | Some(RepresentationReply.Human {
                                                 Outcome = HumanReadOutcome.Succeeded {
                                                                                          Context = InteractionContext.Case current
                                                                                      }
                                             }) ->
                let target = CaseSelector.Id current.CaseId

                match act with
                | DraftAct.Note act ->
                    let input =
                        OperationInput.Note
                            {
                                OperationId = draft.SuppliedId |> Option.defaultWith newId
                                Target = target
                                Act = act
                            }

                    return! review console transport draft input installation token
                | DraftAct.Reminder act ->
                    let submit revision =
                        let input =
                            OperationInput.Reminder
                                {
                                    ReminderOperationRequest.OperationId =
                                        draft.SuppliedId |> Option.defaultWith newId
                                    Target = target
                                    Act = reminderAct revision act
                                }

                        review console transport draft input installation token

                    match reminderIdentity act with
                    | None -> return! submit 0L
                    | Some id ->
                        let frame =
                            invocation
                                Api.reminder
                                {
                                    ReminderGetRequest.Target = target
                                    ReminderId = id
                                }

                        let frame = Frames.withRepresentation Representation.Human frame
                        let! response = transport.Exchange frame token

                        match unpack Api.reminder response with
                        | Some(RepresentationReply.Human {
                                                             Outcome = HumanReadOutcome.Succeeded {
                                                                                                      Context = InteractionContext.Reminder value
                                                                                                  }
                                                         }) ->
                            return! submit (Revision.value value.Revision)
                        | _ ->
                            transport.Deliver frame response
                            return response.ExitCode
                | DraftAct.Case act ->
                    let input =
                        OperationInput.Case
                            {
                                CaseOperationRequest.OperationId =
                                    draft.SuppliedId |> Option.defaultWith newId
                                Target = target
                                ExpectedRevision = Revision.value current.Revision
                                Act = act
                            }

                    return! review console transport draft input installation token
                | DraftAct.Work act ->
                    let input =
                        OperationInput.Work
                            {
                                WorkOperationRequest.OperationId =
                                    draft.SuppliedId |> Option.defaultWith newId
                                Target = target
                                ExpectedRevision = Revision.value current.WorkRevision
                                Act = act
                            }

                    return! review console transport draft input installation token
            | _ ->
                transport.Deliver lookup response
                return response.ExitCode
        }


    let run newId console transport (draft: InteractiveDraft) token =
        match Validation.syntaxIntent (authoredIntent draft) with
        | None -> runValidated newId console transport draft token
        | Some issues ->
            DiagnosticText.refusalLines draft.Language (Refusal.InvalidInput issues)
            |> String.concat "\n"
            |> fun text -> Encoding.UTF8.GetBytes(text + "\n") |> console.Write

            Task.FromResult(ExitCode.value ExitCode.Rejected)
