namespace ClaimCore.Domain

open System

type WorkSubjectState =
    {
        CaseId: CaseId
        Revision: Revision
        Snapshot: CaseWorkSnapshot option
        ChangedAt: DateTimeOffset option
        RecordedBy: ActorId option
        SummaryChangedAt: DateTimeOffset option
        SummaryRecordedBy: ActorId option
        Erased: bool
    }

type WorkSubjectRows =
    {
        CaseId: CaseId
        Revision: Revision
        Snapshot: CaseWorkSnapshot option
        Responsible: ActorId option
        Summary: HandlingSummary option
        SearchText: string option
        ChangedAt: DateTimeOffset option
        RecordedBy: ActorId option
        SummaryChangedAt: DateTimeOffset option
        SummaryRecordedBy: ActorId option
    }

type WorkSubjectCommand =
    {
        ExpectedRevision: Revision
        Update: ValidCaseWorkUpdate
    }

[<NoEquality; NoComparison>]
type WorkSubjectDependencies =
    {
        Parent: CaseSubjectState
        AssigneeEligible: ActorId -> bool
    }

module WorkSubject =
    let initial caseId =
        {
            CaseId = caseId
            Revision = Revision.zero
            Snapshot =
                Some
                    {
                        CaseId = caseId
                        Revision = Revision.zero
                        Work = CaseWork.empty
                    }
            ChangedAt = None
            RecordedBy = None
            SummaryChangedAt = None
            SummaryRecordedBy = None
            Erased = false
        }

    let decide
        context
        operation
        (dependencies: WorkSubjectDependencies)
        (state: WorkSubjectState option)
        command
        =
        DecisionContext.admit
            context
            operation
            (DecisionContext.workAccess context dependencies.Parent.CaseId)
            (fun () ->
                dependencies.Parent.Snapshot
                |> Option.bind CaseWorkPolicy.parentRefusal
                |> Option.map (Refusal.Case >> Error)
                |> Option.defaultValue (Ok()))
        |> Result.bind (fun () ->
            match dependencies.Parent.Snapshot, state |> Option.bind _.Snapshot with
            | Some parent, Some before ->
                let eligible =
                    match command.Update.Responsible with
                    | WorkValue.Set id -> dependencies.AssigneeEligible id
                    | _ -> true

                CaseWorkDecision.decide
                    parent
                    eligible
                    command.ExpectedRevision
                    command.Update
                    before
            | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable))

    let private applySnapshot (before: CaseWorkSnapshot) (after: CaseWorkSnapshot) =
        if
            before.CaseId = after.CaseId
            && Revision.value before.Revision < Revision.Maximum
            && after.Revision = Revision.successor before.Revision
        then
            Ok after
        else
            Error RecordViolation.InvalidSubjectState

    /// Validate retained command/effect agreement using permanent application, not current eligibility.
    let applyRecorded caseId expected update (before: CaseWorkSnapshot) (after: CaseWorkSnapshot) =
        CaseWorkDecision.transition expected update before
        |> Result.mapError (fun _ -> RecordViolation.InvalidSubjectState)
        |> Result.bind (fun proposed ->
            if caseId = before.CaseId && proposed = after then
                applySnapshot before after
            else
                Error RecordViolation.InvalidSubjectState)

    let apply
        (recordedAt: DateTimeOffset)
        (recordedBy: ActorId)
        (state: WorkSubjectState option)
        (effect: CaseWorkSnapshot)
        =
        let before =
            state
            |> Option.defaultWith (fun () -> invalidOp "Work requires its case-created root.")

        before.Snapshot
        |> Option.map (fun snapshot -> applySnapshot snapshot effect)
        |> Option.defaultValue (Error RecordViolation.InvalidSubjectState)
        |> Result.defaultWith (SubjectStateInvalid >> raise)
        |> ignore

        let summaryAt, summaryBy =
            match effect.Work.Summary with
            | None -> None, None
            | Some summary when
                before.Snapshot |> Option.bind (fun value -> value.Work.Summary) = Some summary
                ->
                before.SummaryChangedAt, before.SummaryRecordedBy
            | Some _ -> Some recordedAt, Some recordedBy

        Some
            {
                CaseId = effect.CaseId
                Revision = effect.Revision
                Snapshot = Some effect
                ChangedAt = Some recordedAt
                RecordedBy = Some recordedBy
                SummaryChangedAt = summaryAt
                SummaryRecordedBy = summaryBy
                Erased = false
            }

    let applyErased
        context
        (stamp: SubjectStamp)
        (parent: CaseSubjectState)
        revision
        (state: WorkSubjectState option)
        =
        DecisionContext.admit
            context
            stamp.OperationId
            (DecisionContext.workAccess context parent.CaseId)
            (fun () -> Ok())
        |> Result.mapError (fun _ -> RecordViolation.InvalidSubjectState)
        |> Result.bind (fun () ->
            match state with
            | Some before when
                parent.Lifecycle = Lifecycle.Active
                && before.CaseId = parent.CaseId
                && Revision.value before.Revision < Revision.Maximum
                && revision = Revision.successor before.Revision
                ->
                Ok(
                    Some
                        { before with
                            Revision = revision
                            Snapshot = None
                            ChangedAt = Some stamp.RecordedAt
                            RecordedBy = Some stamp.ActorId
                            SummaryChangedAt = None
                            SummaryRecordedBy = None
                        }
                )
            | _ -> Error RecordViolation.InvalidSubjectState)

    let erase (state: WorkSubjectState) =
        { state with
            Snapshot = None
            SummaryChangedAt = None
            SummaryRecordedBy = None
            Erased = true
        }

    let columns (snapshot: CaseWorkSnapshot option) =
        let work = snapshot |> Option.map _.Work
        let summary = work |> Option.bind _.Summary

        work |> Option.bind _.Responsible,
        summary,
        summary |> Option.map (HandlingSummary.format >> TextSearch.normalize)

    let rows (state: WorkSubjectState) =
        if state.Snapshot.IsNone && not state.Erased then
            None
        else
            let responsible, summary, search = columns state.Snapshot

            Some
                {
                    CaseId = state.CaseId
                    Revision = state.Revision
                    Snapshot = state.Snapshot
                    Responsible = responsible
                    Summary = summary
                    SearchText = search
                    ChangedAt = state.ChangedAt
                    RecordedBy = state.RecordedBy
                    SummaryChangedAt = state.SummaryChangedAt
                    SummaryRecordedBy = state.SummaryRecordedBy
                }

    let restore caseId lifecycle (row: WorkSubjectRows) =
        let responsible, summary, search = columns row.Snapshot

        let attributed =
            if row.Revision = Revision.zero then
                row.ChangedAt.IsNone && row.RecordedBy.IsNone
            else
                row.ChangedAt.IsSome && row.RecordedBy.IsSome

        let privatePresent =
            if lifecycle = Lifecycle.Erased then
                row.Snapshot.IsNone
            else
                row.Snapshot.IsSome

        let matchingSnapshot =
            row.Snapshot
            |> Option.forall (fun value ->
                value.CaseId = caseId
                && value.Revision = row.Revision
                && (row.Revision <> Revision.zero || value.Work = CaseWork.empty))

        let valid =
            row.CaseId = caseId
            && attributed
            && privatePresent
            && matchingSnapshot
            && ((responsible, summary, search) = (row.Responsible, row.Summary, row.SearchText))
            && (row.Summary.IsSome = row.SummaryChangedAt.IsSome)
            && (row.SummaryChangedAt.IsSome = row.SummaryRecordedBy.IsSome)

        if not valid then
            Error RecordViolation.InvalidSubjectState
        else
            Ok
                {
                    WorkSubjectState.CaseId = caseId
                    Revision = row.Revision
                    Snapshot = row.Snapshot
                    ChangedAt = row.ChangedAt
                    RecordedBy = row.RecordedBy
                    SummaryChangedAt = row.SummaryChangedAt
                    SummaryRecordedBy = row.SummaryRecordedBy
                    Erased = lifecycle = Lifecycle.Erased
                }
