namespace ClaimCore.Cli

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
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
    | Work of AuthoredWorkAct
    | Reminder of ReminderAct<int64 option>

/// A structurally checked draft has no executable revision or automatically allocated operation id.
[<NoEquality; NoComparison>]
type internal InteractiveDraft =
    private
        {
            Reference: string
            Act: DraftAct
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
        OperationCatalog.mutations
        |> List.tryFind (fun entry ->
            let words = CommandGrammar.words entry

            words = List.truncate words.Length arguments
            && match entry.SelectedAct with
               | Some(ActKind.Case kind) ->
                   (CaseActKind.describe kind).Revision = RevisionInput.Expected
               | Some(ActKind.Work _)
               | Some(ActKind.Reminder _) -> true
               | _ -> false)

    let private keepGroups entry arguments =
        let inputs, _ = CommandGrammar.derive entry

        let defaults =
            inputs
            |> List.collect (fun input ->
                let flag = "--" + CommandGrammar.kebab (CommandGrammar.inputName input.Name)

                match input.Binding.Kind with
                | BindingKind.Group(_, choices) when
                    List.contains "KEEP" choices && not (hasOption flag arguments)
                    ->
                    [
                        flag
                        CommandGrammar.kebab (
                            CorrectionGroupAction.token CorrectionGroupAction.Keep
                        )
                    ]
                | _ -> [])

        arguments @ defaults

    let prepare console arguments =
        if
            not console.IsInteractive
            || not (requestedText arguments)
            || hasOption "--expected-revision" arguments
            || hasOption "--expected-work-revision" arguments
            || hasOption "--expected-reminder-revision" arguments
            || hasOption "--dry-run" arguments
        then
            Ok None
        else
            match operation arguments with
            | None -> Ok None
            | Some entry ->
                let intent =
                    match entry.SelectedAct with
                    | Some(ActKind.Reminder _) ->
                        Grammar.reminderIntent (keepGroups entry arguments)
                        |> Result.map (fun ((reference, act, supplied), language, preview) ->
                            (reference, DraftAct.Reminder act, supplied), language, preview)
                    | Some(ActKind.Work _) ->
                        Grammar.workIntent (keepGroups entry arguments)
                        |> Result.map (fun ((reference, act, supplied), language, preview) ->
                            (reference, DraftAct.Work act, supplied), language, preview)
                    | _ ->
                        Grammar.caseIntent (keepGroups entry arguments)
                        |> Result.map (fun ((reference, act, supplied), language, preview) ->
                            (reference, DraftAct.Case act, supplied), language, preview)

                intent
                |> Result.map (fun ((reference, act, supplied), language, preview) ->
                    Some
                        {
                            Reference =
                                match reference with
                                | CaseSelector.Reference reference -> reference
                                | CaseSelector.Id _ ->
                                    invalidOp
                                        "Interactive reference bindings must supply a reference."
                            Act = act
                            SuppliedId = supplied
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
        (input: OperationInput)
        installation
        token
        =
        task {
            let preview = invocation Api.operationPreview input
            let! response = transport.Exchange preview token
            transport.Deliver preview response

            match unpack Api.operationPreview response with
            | Some {
                       Outcome = Outcome.Succeeded(Previewed.Reviewed _)
                   } when not draft.PreviewOnly ->
                MessageCatalogs.renderCli draft.Language CliMessage.ReviewConfirmation
                |> Encoding.UTF8.GetBytes
                |> console.Write

                if CliConfirmation.confirmed draft.Language (console.ReadLine()) then
                    let execute =
                        invocation Api.operationExecute input
                        |> Frames.bindInstallation installation
                        |> Result.defaultWith (fun _ ->
                            invalidOp "Reviewed scope must bind the request.")

                    let! accepted = transport.Exchange execute token
                    transport.Deliver execute accepted
                    do! WorkRefresh.run transport.Exchange transport.Deliver accepted token
                    return accepted.ExitCode
                else
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

    let rec run newId console transport (draft: InteractiveDraft) token =
        task {
            let context = invocation Api.context ()
            let! contextReply = transport.Exchange context token

            match unpack Api.context contextReply with
            | Some(Outcome.Succeeded scope) ->
                return! readCase newId console transport draft scope.InstallationId token
            | _ ->
                transport.Deliver context contextReply
                return contextReply.ExitCode
        }

    and private readCase newId console transport (draft: InteractiveDraft) installation token =
        task {
            let lookup =
                invocation
                    Api.caseGet
                    {
                        CaseGetRequest.Target = CaseSelector.Reference draft.Reference
                    }

            let! response = transport.Exchange lookup token

            match unpack Api.caseGet response with
            | Some(Outcome.Succeeded current) ->
                let target = CaseSelector.Id current.Snapshot.CaseId

                match draft.Act with
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

                        let! response = transport.Exchange frame token

                        match unpack Api.reminder response with
                        | Some(Outcome.Succeeded value) ->
                            return! submit (Reminder.revision value.Item.Reminder |> Revision.value)
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
                                ExpectedRevision = Revision.value current.Snapshot.Revision
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
                                ExpectedRevision = Revision.value current.Work.Revision
                                Act = act
                            }

                    return! review console transport draft input installation token
            | _ ->
                transport.Deliver lookup response
                return response.ExitCode
        }
