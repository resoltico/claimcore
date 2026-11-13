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

    let decide (dependencies: WorkSubjectDependencies) (state: WorkSubjectState option) command =
        match dependencies.Parent.Snapshot, state |> Option.bind _.Snapshot with
        | Some parent, Some before ->
            let eligible =
                match command.Update.Responsible with
                | WorkValue.Set id -> dependencies.AssigneeEligible id
                | _ -> true

            CaseWorkDecision.decide parent eligible command.ExpectedRevision command.Update before
        | _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)

    let apply
        (recordedAt: DateTimeOffset)
        (recordedBy: ActorId)
        (state: WorkSubjectState option)
        (effect: CaseWorkSnapshot)
        =
        let before =
            state
            |> Option.defaultWith (fun () -> invalidOp "Work requires its case-created root.")

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
        (stamp: SubjectStamp)
        (parent: CaseSubjectState)
        revision
        (state: WorkSubjectState option)
        =
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
        | _ -> Error RecordViolation.InvalidSubjectState

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
