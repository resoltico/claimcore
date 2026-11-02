namespace ClaimCore.Application

open System
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Current read authority and date basis are acquired with every page; continuations grant no access.
module internal WorkQueries =
    let private scope parts = String.concat "\u001f" parts |> Encoding.UTF8.GetBytes |> SHA256.HashData |> Convert.ToHexStringLower

    let private selection (services: ServiceContext) (tx: IReadTx) actor mine =
        QueryResults.currentActor services.Principal actor
        |> Result.bind (fun _ ->
            match Authority.visibility services.Principal actor, actor.Actor with
            | Some visibility, Some requester ->
                Ok { CaseSelection.DateBasis = CaseWorkReads.basis services tx.ObservedAt; Visibility = visibility; ActorId = requester.ActorId; ReadRoles = Authority.readRoles services.Principal; ForbiddenRoles = Authority.invalidRoles services.Principal; Filter = { CaseListFilter.empty with Assignment = if mine then Some CaseAssignment.Mine else None } }
            | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable))

    let overview (services: ServiceContext) (request: WorkOverviewRequest) token =
        task {
            let! result = services.Store.Read(
                { Work = fun tx -> task {
                    let! actor = tx.Authority { Principal = services.Principal; CaseId = None }
                    match selection services tx actor request.Mine with
                    | Error refusal -> return Outcome.Rejected refusal
                    | Ok selection ->
                        let! overview = tx.WorkOverview(selection, request.Mine)
                        return Outcome.Succeeded overview }
                  CancellationToken = token })
            return QueryResults.readResult result
        }

    let private reminderActs principal actor maintenance lifecycle status value =
        let permitted = not maintenance && lifecycle = Lifecycle.Active && Authority.can principal actor Capability.ReadCase (Scope.Case(Reminder.caseId value)) && Authority.can principal actor Capability.ManageCaseWork (Scope.Case(Reminder.caseId value))
        if not permitted then []
        else
            match Reminder.state value, status with
            | ReminderState.Pending, Some CaseStatus.Opened -> [ ReminderActKind.Update; ReminderActKind.Complete; ReminderActKind.CompleteAndPlanNext; ReminderActKind.Cancel ]
            | ReminderState.Pending, _ -> [ ReminderActKind.Complete; ReminderActKind.Cancel ]
            | (ReminderState.Completed | ReminderState.Cancelled), Some CaseStatus.Opened -> [ ReminderActKind.Reopen ]
            | _ -> []

    let reminders (services: ServiceContext) (request: ReminderListRequest) token =
        task {
            if not(QueryResults.validLimit request.Limit) then return QueryResults.invalidLimit()
            else
                let text = match request.Text with None -> Ok None | Some raw -> CaseSearchText.parse raw |> Result.map Some |> Result.mapError(fun issue -> Refusal.invalidInput(InputTarget.Text,issue))
                match text with
                | Error refusal -> return Outcome.Rejected refusal
                | Ok text ->
                    let! result = services.Store.Read(
                        { Work = fun tx -> task {
                            let! actor = tx.Authority { Principal = services.Principal; CaseId = None }
                            let! parent = match request.Target with Some target -> ExecutionFacts.findCase tx target | None -> System.Threading.Tasks.Task.FromResult None
                            let targetAllowed = match request.Target,parent with None,_ -> true | Some _,Some parent -> Authority.can services.Principal actor Capability.ReadCase (Scope.Case parent.CaseId) | _ -> false
                            match selection services tx actor (request.Assignment = Some CaseAssignment.Mine) with
                            | Error refusal -> return Outcome.Rejected refusal
                            | Ok _ when not targetAllowed -> return QueryResults.unavailable
                            | Ok selection ->
                                let selection = { selection with Filter = { selection.Filter with Assignment = request.Assignment } }
                                let state = defaultArg request.State ReminderState.Pending
                                let parentId = parent |> Option.map _.CaseId
                                let binding = scope [ ActorId.format selection.ActorId; string selection.Visibility; string selection.ReadRoles; string selection.ForbiddenRoles; parentId |> Option.map CaseId.format |> Option.defaultValue ""; ReminderState.token state; request.Due |> Option.map ReminderDue.token |> Option.defaultValue ""; request.Assignment |> Option.map CaseAssignment.token |> Option.defaultValue ""; text |> Option.map CaseSearchText.value |> Option.defaultValue ""; string request.IncludeInactive ]
                                match QueryResults.cursor (Cursors.decodeReminders services.InstallationId binding) request.AfterCursor with
                                | Error refusal -> return Outcome.Rejected refusal
                                | Ok after ->
                                    let! maintenance = tx.Maintenance()
                                    let! page = tx.ReminderPage { Selection = selection; TargetCaseId = parentId; State = state; Due = request.Due; Text = text; IncludeInactive = request.IncludeInactive; After = after |> Option.map(fun (due,id) -> { ReminderPosition.Due = due; ReminderId = id }); Limit = request.Limit }
                                    let items = page.Items |> List.map(fun row ->
                                        match row.Stored.Snapshot with
                                        | None -> Error CoreFault.StoreIntegrityError
                                        | Some reminder ->
                                            Ok { ReminderReadModel.Reminder = reminder; Reference = row.Reference; RecordedAt = row.Stored.RecordedAt; RecordedBy = row.Stored.RecordedBy; Acts = reminderActs services.Principal actor maintenance.Active row.Lifecycle row.Status reminder; Active = row.Lifecycle = Lifecycle.Active && row.Status = Some CaseStatus.Opened }) |> QueryResults.collect
                                    return items |> Result.map(fun items -> { ReminderPage.Basis = selection.DateBasis; Items = items; NextCursor = page.Next |> Option.map(fun position -> Cursors.encodeReminders services.InstallationId binding position.Due position.ReminderId) }) |> Result.map Outcome.Succeeded |> Result.defaultWith Outcome.Failed }
                          CancellationToken = token })
                    return QueryResults.readResult result
        }

    let activity (services: ServiceContext) (request: CaseActivityRequest) token =
        task {
            if not(QueryResults.validLimit request.Limit) then return QueryResults.invalidLimit()
            else
                let! result = services.Store.Read(
                    { Work = fun tx -> task {
                        let! view = ExecutionFacts.case services tx None request.Target
                        match view.Case with
                        | Some parent when Authority.can services.Principal view.Actor (if request.Detail = HistoryDetail.Full then Capability.ReadHistoryFull else Capability.ReadHistory) (Scope.Case parent.CaseId) ->
                            let actor = view.Actor.Actor.Value.ActorId
                            let binding = scope [ CaseId.format parent.CaseId; ActorId.format actor; string request.Detail ]
                            match QueryResults.cursor (Cursors.decodeActivity services.InstallationId binding) request.AfterCursor with
                            | Error refusal -> return Outcome.Rejected refusal
                            | Ok before ->
                                let! page = tx.ActivityPage { CaseId = parent.CaseId; BeforeSequence = before; Limit = request.Limit }
                                let mutable items = Ok []
                                for entry in page.Items do
                                    let subject =
                                        match entry.Subject with
                                        | EntryTarget.Case(id, revision, act, _) when id = parent.CaseId -> Ok(ActivitySubject.Case(revision,act))
                                        | EntryTarget.Work subject when subject.CaseId = parent.CaseId -> Ok(ActivitySubject.Work(subject.Revision,subject.Act))
                                        | EntryTarget.Reminder subject when subject.CaseId = parent.CaseId -> Ok(ActivitySubject.Reminder(subject.Act,subject.ReminderId,subject.Revision,subject.NextReminderId |> Option.map(fun id -> id,Revision.first)))
                                        | _ -> Error CoreFault.StoreIntegrityError
                                    let! _, _, current = ExecutionFacts.operation tx services.Principal entry.OperationId
                                    let detail =
                                        LedgerEntries.validate services.InstallationId entry
                                        |> Result.bind (fun validated -> if request.Detail = HistoryDetail.Full then LedgerEntries.receipt services.Principal view.Actor current validated |> Result.map Some else Ok None)
                                    items <- items |> Result.bind(fun items ->
                                        match subject,detail with
                                        | Ok subject,Ok detail -> Ok({ CaseActivity.OperationId = entry.OperationId; Position = LedgerEntries.position entry.Sequence entry.EntryHash; Subject = subject; RecordedAt = entry.RecordedAt; RecordedBy = entry.ActorId; Detail = detail }::items)
                                        | Error fault,_ | _,Error fault -> Error fault)
                                return items |> Result.map(fun items -> { CaseActivityPage.Items = List.rev items; NextCursor = page.Next |> Option.map(Cursors.encodeActivity services.InstallationId binding) }) |> Result.map Outcome.Succeeded |> Result.defaultWith Outcome.Failed
                        | _ -> return QueryResults.unavailable }
                      CancellationToken = token })
                return QueryResults.readResult result
        }
