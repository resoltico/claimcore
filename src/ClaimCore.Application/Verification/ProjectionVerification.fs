namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open System
open ClaimCore.Application.VerificationChecks

/// Projections are comparison targets only after the ledger has been folded.
module ProjectionVerification =
    /// Only these already-decoded case columns participate in independent work-index validation.
    type CaseWorkIndex =
        {
            CaseId: CaseId
            Snapshot: CaseSnapshot
            ChangedAt: DateTimeOffset
            Responsible: ActorId option
            FollowUp: CalendarDate option
            SearchText: string option
        }

    let caseWorkIndex (record: StoredCase) =
        {
            CaseId = record.CaseId
            Snapshot = record.Snapshot
            ChangedAt = record.Index.ChangedAt
            Responsible = record.Index.Responsible
            FollowUp = record.Index.FollowUp
            SearchText = record.Index.WorkSearchText
        }

    let caseProjection installationId histories seen (record: StoredCase) =
        result {
            do!
                require
                    None
                    VerificationFinding.CaseProjection
                    (not (Set.contains record.CaseId seen)
                     && CaseReplay.matchesProjection installationId histories record)

            let! history =
                Map.tryFind record.CaseId histories
                |> known None VerificationFinding.CaseProjection

            let expected = CaseIndex.create history.ChangedAt record.Snapshot None []

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

            do!
                require
                    None
                    VerificationFinding.CasePaidAmountProjection
                    (Option.map Money.value record.Index.Paid = Option.map Money.value expected.Paid)

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

    let workIndexes
        (work: Map<CaseId, WorkHistory>)
        (reminders: Map<ReminderId, ReminderHistory>)
        (records: CaseWorkIndex list)
        =
        records
        |> List.fold
            (fun state record ->
                state
                |> Result.bind (fun () ->
                    let current = work |> Map.tryFind record.CaseId |> Option.bind _.Snapshot

                    let pendingDue =
                        reminders
                        |> Map.values
                        |> Seq.choose _.Snapshot
                        |> Seq.filter (fun value ->
                            Reminder.caseId value = record.CaseId
                            && Reminder.state value = ReminderState.Pending)
                        |> Seq.map Reminder.due
                        |> Seq.toList

                    let expected =
                        CaseIndex.create record.ChangedAt record.Snapshot current pendingDue

                    require
                        None
                        VerificationFinding.CaseWorkProjection
                        (record.Responsible = expected.Responsible
                         && record.FollowUp = expected.FollowUp
                         && record.SearchText = expected.WorkSearchText)))
            (Ok())

    let workIndex work reminders record =
        workIndexes work reminders [ caseWorkIndex record ]

    /// A row is compared without rebuilding or reinterpreting any business rule.
    let row finding key expected seen actual =
        let id = key actual

        require None finding (not (Set.contains id seen) && Map.tryFind id expected = Some actual)
        |> Result.map (fun () -> Set.add id seen)

    let finishRows finding expected seen =
        require None finding (Map.count expected = Set.count seen)

    let rows finding key expected actual =
        actual
        |> List.fold
            (fun seen actual ->
                seen |> Result.bind (fun seen -> row finding key expected seen actual))
            (Ok Set.empty)
        |> Result.bind (finishRows finding expected)
