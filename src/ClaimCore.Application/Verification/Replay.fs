namespace ClaimCore.Application.Verification

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Application.Execution
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat
open ClaimCore.Application.Verification.VerificationChecks

/// Verification adds checkpoint/finding context to the single application fold.
module internal Replay =
    type Scan =
        {
            Fold: FoldState
            ExpectedSeen: bool
        }

        member this.Head = this.Fold.Head

    let private finding =
        function
        | FoldProblem.Link -> VerificationFinding.Link
        | FoldProblem.Identity
        | FoldProblem.Task -> VerificationFinding.PayloadFormat
        | FoldProblem.Executor ExecutorIssue.ActorNotEnabled -> VerificationFinding.ActorNotEnabled
        | FoldProblem.Executor ExecutorIssue.HumanRequired -> VerificationFinding.HumanRequired
        | FoldProblem.Executor ExecutorIssue.OwnerRequired -> VerificationFinding.OwnerRequired
        | FoldProblem.Decision -> VerificationFinding.StateSequence
        | FoldProblem.State RecordViolation.InvalidRevisionSequence ->
            VerificationFinding.RevisionSequence
        | FoldProblem.State RecordViolation.InvalidHistoryStart -> VerificationFinding.HistoryStart
        | FoldProblem.State _
        | FoldProblem.NaturalKey -> VerificationFinding.StateSequence
        | FoldProblem.Nonce -> VerificationFinding.Commitment
        | FoldProblem.Erasure -> VerificationFinding.PayloadWithoutErasure
        | FoldProblem.ErasureIncomplete -> VerificationFinding.ErasureIncomplete
        | FoldProblem.TargetHumanRequired -> VerificationFinding.HumanRequired

    let initial (expected: LedgerPosition option) =
        {
            Fold = Fold.initial
            ExpectedSeen = expected |> Option.exists ((=) LedgerPosition.genesis)
        }

    let link (state: Scan) sequence previous =
        require
            (Some sequence)
            VerificationFinding.Link
            (Sequence.value state.Head.Sequence < Sequence.Maximum
             && sequence = Sequence.successor state.Head.Sequence
             && previous = EntryHash.toBytes state.Head.EntryHash)

    let append installation expected state record =
        Fold.append installation state.Fold record
        |> Result.mapError (fun problem ->
            divergence (Some record.Root.Header.Sequence) (finding problem))
        |> Result.map (fun next ->
            {
                Fold = next
                ExpectedSeen = state.ExpectedSeen || (expected |> Option.exists ((=) next.Head))
            })

    let erasure state =
        Fold.complete state.Fold
        |> Result.map ignore
        |> Result.mapError (fun problem -> divergence None (finding problem))

    let head (state: Scan) actual =
        require None VerificationFinding.Head (actual = Some state.Head)

    let complete (expected: LedgerPosition option) (state: Scan) =
        if expected.IsSome && not state.ExpectedSeen then
            Error(
                divergence (expected |> Option.map _.Sequence) VerificationFinding.ExpectedPosition
            )
        else
            Ok state.Head

    let familyRows state =
        state.Fold.Families
        |> Map.toList
        |> List.fold
            (fun result (id, family) ->
                result
                |> Result.bind (fun rows ->
                    let parts =
                        state.Fold.Parts
                        |> Map.values
                        |> Seq.filter (fun change ->
                            change.Aggregate = AggregateReference.Family id)
                        |> Seq.toList

                    ProjectionRows.family
                        family
                        parts
                        state.Fold.Origins
                        (Map.tryFind id state.Fold.Summaries)
                    |> Result.mapError (fun _ ->
                        divergence None VerificationFinding.CaseProjection)
                    |> Result.map (fun row -> Map.add id row rows)))
            (Ok Map.empty)

    let peopleRows state =
        state.Fold.People
        |> Map.toList
        |> List.fold
            (fun result (id, person) ->
                result
                |> Result.bind (fun rows ->
                    ProjectionRows.person person
                    |> Result.mapError (fun _ ->
                        divergence None VerificationFinding.PeopleProjection)
                    |> Result.map (fun row -> Map.add id row rows)))
            (Ok Map.empty)

    let caseRecords state =
        familyRows state
        |> Result.map (
            Map.map (fun id row ->
                {
                    StoredCase.CaseId = id
                    Snapshot = row.Snapshot
                    SnapshotBytes = CaseRecord.encodeSnapshot row.Snapshot
                    Index = row.Index
                })
        )

    let workRows state =
        familyRows state
        |> Result.map (
            Map.map (fun id row ->
                let work = row.Work

                {
                    StoredWork.CaseId = id
                    Revision = work.Revision
                    Snapshot = work.Snapshot
                    SnapshotBytes =
                        work.Snapshot
                        |> Option.map WorkRecord.encodeSnapshot
                        |> Option.defaultValue [||]
                    ChangedAt = work.Changed |> Option.map _.RecordedAt
                    RecordedBy = work.Changed |> Option.map _.RecordedBy
                    SummaryChangedAt = work.SummaryChanged |> Option.map _.RecordedAt
                    SummaryRecordedBy = work.SummaryChanged |> Option.map _.RecordedBy
                })
        )

    let reminderRows state =
        familyRows state
        |> Result.map (fun rows ->
            rows
            |> Map.toList
            |> List.collect (fun (caseId, row) ->
                row.Reminders
                |> List.map (fun reminder ->
                    let snapshot =
                        Reminder.ofValue reminder.Id caseId reminder.Revision reminder.Value

                    reminder.Id,
                    {
                        StoredReminder.ReminderId = reminder.Id
                        CaseId = caseId
                        Revision = reminder.Revision
                        Snapshot = snapshot
                        SnapshotBytes =
                            snapshot
                            |> Option.map ReminderRecord.encodeSnapshot
                            |> Option.defaultValue [||]
                        RecordedAt = reminder.Changed.RecordedAt
                        RecordedBy = reminder.Changed.RecordedBy
                    }))
            |> Map.ofList)

    let noteRows state =
        familyRows state
        |> Result.map (fun rows ->
            rows
            |> Map.toList
            |> List.collect (fun (caseId, row) ->
                row.Notes
                |> List.map (fun note ->
                    note.Id,
                    {
                        StoredNote.Note = Note.ofAccepted note.Id caseId note.Value.Text
                        CreatedPosition = note.Origin.Position
                        RecordedAt = note.Origin.RecordedAt
                        RecordedBy = ProjectionRows.familyActor note.Origin.Authority
                    }))
            |> Map.ofList)

    let partyRows state =
        familyRows state
        |> Result.map (fun rows ->
            rows
            |> Map.toList
            |> List.collect (fun (caseId, row) ->
                row.Parties
                |> List.map (fun party ->
                    party.Id,
                    {
                        StoredParty.PartyId = party.Id
                        CaseId = caseId
                        Revision = party.Revision
                        CreatedPosition = party.Origin.Position
                        Value = party.Value
                    }))
            |> Map.ofList)

    let payments state =
        familyRows state
        |> Result.map (fun rows ->
            rows
            |> Map.toList
            |> List.collect (fun (caseId, row) ->
                row.Payments
                |> List.map (fun payment ->
                    let content =
                        match payment.Value.Entry with
                        | Some entry ->
                            PaymentProjectionContent.Available(entry, payment.Value.Removed)
                        | None -> PaymentProjectionContent.Cleared payment.Value.Removed

                    payment.Id,
                    {
                        PaymentProjectionRow.CaseId = caseId
                        PaymentId = payment.Id
                        Position = payment.Origin.Position
                        Content = content
                    }))
            |> Map.ofList)
