module ClaimCore.TestSupport.Inputs

open ClaimCore.Domain
open ClaimCore.TestSupport.DomainInputs
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

let activeContext (reply: ContextReply) =
    match reply.Requester with
    | RequesterState.Active context -> context
    | other -> failtestf "Fixture requires ACTIVE requester, observed %A" other

let unscheduledWork: CaseWorkView =
    {
        Revision = Revision.zero
        ChangedAt = None
        RecordedBy = None
        Summary = None
        SummaryChangedAt = None
        SummaryRecordedBy = None
        NextAction = None
        PendingReminders = 0L
        OverdueReminders = 0L
        NeedsNextAction = true
        CanUpdateSummary = true
        CanAssign = true
        CanCreateReminder = true
        Responsible = None
        FollowUp = None
        Due = CaseDue.Unscheduled
        Basis = workBasis
    }

/// A fact snapshot has no authored work. Relation/work tests provide their independent work fixture.
let workView (state: CaseState<CaseFields>) : CaseWorkView =
    let fields = CaseState.fields state
    let active = CaseState.lifecycle state = Lifecycle.Active

    let actionable =
        active
        && (fields |> Option.exists (fun fields -> fields.Status = CaseStatus.Opened))

    { unscheduledWork with
        Due =
            CaseDue.at
                workBasis.BusinessDate
                (CaseState.lifecycle state)
                (fields |> Option.map _.Status)
                None
        NeedsNextAction = actionable
        CanUpdateSummary = active
        CanAssign = actionable
        CanCreateReminder = actionable
    }

/// A complete transport fixture; policy tests assert literal reasons from the actual service.
let caseAdvice state =
    let headline =
        state
        |> CaseState.map (fun (fields: CaseFields) ->
            fields.Status, CaseProgress.ofFinancial fields.Finance)
        |> CaseHeadline.ofState

    let blocked =
        (ClaimCore.Domain.Tasks.Registry.clientDeclarations
         |> List.filter (fun declaration -> declaration.Target = ActSubject.Case)
         |> List.map _.Id)
        |> List.map (fun act ->
            {
                CaseTaskRefusal.Act = act
                Reason = Refusal.Case(CaseRefusal.NotOffered headline)
            })

    CaseAdvice.create [] None HistoryAccess.None blocked
    |> Result.defaultWith invalidOp

let caseListPage page =
    {
        CaseListRequest.Page = page
        Filter = CaseListFilter.empty<string>
    }

/// Build a disclosed control for interaction tests; business availability has task-owned tests.
let offeredTask token binding seeds =
    let task = TaskId.ofLiteral token
    let declaration = ClaimCore.Domain.Tasks.Registry.findDeclaration task |> Option.get

    TaskOffer.Available(
        task,
        binding,
        {
            Seeds = seeds
            Selections = Set.empty
            Editable =
                declaration.Inputs
                |> List.filter (fun input -> input.Source = InputSource.Authored)
                |> List.map _.Input
            Choices = Map.empty
            Applicable = true
            Blocked = None
            Recommendation = None
        }
    )
