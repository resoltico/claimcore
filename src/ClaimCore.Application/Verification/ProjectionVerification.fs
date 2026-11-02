namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open System
open ClaimCore.Application.VerificationChecks

/// Projections are comparison targets only after the ledger has been folded.
module ProjectionVerification =
    let actor (actors: Map<ActorId, ActorAuthority>) (row: Actor) =
        result {
            let id = row.ActorId
            let principal = row.Principal

            do!
                require
                    None
                    VerificationFinding.AuthorityProjection
                    (not (Map.containsKey id actors)
                     && not (
                         actors
                         |> Map.values
                         |> Seq.exists (fun actor -> actor.Principal = principal)
                     ))

            let actor: ActorAuthority =
                {
                    Actor =
                        {
                            ActorId = id
                            Principal = principal
                            Enabled = row.Enabled
                        }
                    Grants = []
                }

            return Map.add id actor actors
        }

    let grant (actors: Map<ActorId, ActorAuthority>) (id, role, caseId) =
        result {
            let! actor =
                Map.tryFind id actors |> known None VerificationFinding.AuthorityProjection

            let scope =
                caseId |> Option.map Scope.Case |> Option.defaultValue Scope.Installation

            let grant: ActorGrant = { Role = role; Scope = scope }

            do!
                require
                    None
                    VerificationFinding.AuthorityProjection
                    (not (List.contains grant actor.Grants))

            return
                Map.add
                    id
                    { actor with
                        Grants = grant :: actor.Grants
                    }
                    actors
        }

    let private matching expected actual =
        Map.count expected = Map.count actual
        && (expected
            |> Map.forall (fun id (actor: ActorAuthority) ->
                match Map.tryFind id actual with
                | None -> false
                | Some current ->
                    { actor with Grants = [] } = { current with Grants = [] }
                    && Set.ofList actor.Grants = Set.ofList current.Grants))

    let caseProjection installationId histories seen (record: StoredCase) =
        result {
            do!
                require
                    None
                    VerificationFinding.CaseProjection
                    (not (Set.contains record.CaseId seen)
                     && CaseFold.matchesProjection installationId histories record)

            let! history =
                Map.tryFind record.CaseId histories
                |> known None VerificationFinding.CaseProjection

            let expected = CaseIndex.create history.ChangedAt record.Snapshot

            do!
                require
                    None
                    VerificationFinding.CaseStatusProjection
                    (record.Index.Status = expected.Status)

            do!
                require
                    None
                    VerificationFinding.CaseProgressProjection
                    (record.Index.Progress = expected.Progress)

            do!
                require
                    None
                    VerificationFinding.CaseChangedAtProjection
                    (record.Index.ChangedAt = expected.ChangedAt)

            do!
                require
                    None
                    VerificationFinding.CaseSearchProjection
                    (record.Index.SearchText = expected.SearchText)

            do!
                require
                    None
                    VerificationFinding.CaseNotifiedOnProjection
                    (record.Index.NotifiedOn = expected.NotifiedOn)

            do!
                require
                    None
                    VerificationFinding.CaseClaimedAmountProjection
                    (Option.map Money.value record.Index.Claimed =
                        Option.map Money.value expected.Claimed)

            do!
                require
                    None
                    VerificationFinding.CaseClaimedCurrencyProjection
                    (Option.map Money.currency record.Index.Claimed =
                        Option.map Money.currency expected.Claimed)

            do!
                require
                    None
                    VerificationFinding.CasePayableAmountProjection
                    (Option.map Money.value record.Index.Payable =
                        Option.map Money.value expected.Payable)

            do!
                require
                    None
                    VerificationFinding.CasePayableCurrencyProjection
                    (Option.map Money.currency record.Index.Payable =
                        Option.map Money.currency expected.Payable)

            return Set.add record.CaseId seen
        }

    let finishCases histories seen =
        require None VerificationFinding.CaseProjection (Map.count histories = Set.count seen)

    let cases installationId histories (projections: StoredCase list) =
        require None VerificationFinding.CaseProjection (projections.Length = Map.count histories)
        |> Result.bind (fun () ->
            projections
            |> List.fold
                (fun state record ->
                    state
                    |> Result.bind (fun seen ->
                        caseProjection installationId histories seen record))
                (Ok Set.empty))
        |> Result.bind (finishCases histories)

    let finishAuthority expected actual =
        require None VerificationFinding.AuthorityProjection (matching expected actual)

    let authority expected actorRows grantRows =
        actorRows
        |> List.fold
            (fun state row -> Result.bind (fun state -> actor state row) state)
            (Ok Map.empty)
        |> Result.bind (fun actors ->
            grantRows
            |> List.fold
                (fun state row -> Result.bind (fun state -> grant state row) state)
                (Ok actors))
        |> Result.bind (finishAuthority expected)

    let fences expected actual =
        require None VerificationFinding.FenceProjection (actual = expected)


    let workIndexes
        (work: Map<CaseId, WorkHistory>)
        (reminders: Map<ReminderId, ReminderHistory>)
        (records: StoredCase list)
        =
        let dates =
            reminders
            |> Map.values
            |> Seq.fold
                (fun dates history ->
                    match history.State, history.Snapshot with
                    | ReminderState.Pending, Some value ->
                        let due = Reminder.due value

                        Map.change
                            history.CaseId
                            (fun old ->
                                Some(old |> Option.map (min due) |> Option.defaultValue due))
                            dates
                    | _ -> dates)
                Map.empty

        records
        |> List.fold
            (fun state record ->
                state
                |> Result.bind (fun () ->
                    let responsible =
                        work
                        |> Map.tryFind record.CaseId
                        |> Option.bind _.Snapshot
                        |> Option.bind _.Work.Responsible

                    let search =
                        work
                        |> Map.tryFind record.CaseId
                        |> Option.bind _.Snapshot
                        |> Option.bind _.Work.Summary
                        |> Option.map (HandlingSummary.format >> CaseSearch.normalize)

                    require
                        None
                        VerificationFinding.CaseWorkProjection
                        (record.Index.Responsible = responsible
                         && record.Index.FollowUp = Map.tryFind record.CaseId dates
                         && record.Index.WorkSearchText = search)))
            (Ok())

    let workIndex work reminders record = workIndexes work reminders [ record ]

    let workRows (expected: Map<CaseId, WorkHistory>) (actual: StoredWork list) =
        let matches (row: StoredWork) =
            match Map.tryFind row.CaseId expected with
            | None -> false
            | Some history ->
                row.Revision = history.Revision
                && row.Snapshot = history.Snapshot
                && row.SnapshotBytes =
                    (history.Snapshot
                     |> Option.map ClaimCore.RecordFormat.WorkRecord.encodeSnapshot
                     |> Option.defaultValue Array.empty)
                && row.ChangedAt = history.ChangedAt
                && row.RecordedBy = history.RecordedBy

        require
            None
            VerificationFinding.CaseWorkProjection
            (actual.Length = expected.Count
             && (actual |> List.map _.CaseId |> Set.ofList |> Set.count) = expected.Count
             && List.forall matches actual)

    let reminderRows (expected: Map<ReminderId, ReminderHistory>) (actual: StoredReminder list) =
        let matches (row: StoredReminder) =
            match Map.tryFind row.ReminderId expected with
            | None -> false
            | Some history ->
                row.CaseId = history.CaseId
                && row.Revision = history.Revision
                && row.Snapshot = history.Snapshot
                && row.SnapshotBytes =
                    (history.Snapshot
                     |> Option.map ClaimCore.RecordFormat.ReminderRecord.encodeSnapshot
                     |> Option.defaultValue Array.empty)
                && row.RecordedAt = history.RecordedAt
                && row.RecordedBy = history.RecordedBy

        require
            None
            VerificationFinding.CaseWorkProjection
            (actual.Length = expected.Count
             && (actual |> List.map _.ReminderId |> Set.ofList |> Set.count) = expected.Count
             && List.forall matches actual)
