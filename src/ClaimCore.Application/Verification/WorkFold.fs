namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application.VerificationChecks

type WorkHistory =
    {
        Revision: Revision
        Snapshot: CaseWorkSnapshot option
        ChangedAt: DateTimeOffset option
        RecordedBy: ActorId option
        MissingPayloadAt: Sequence option
        PresentPayloadAt: Sequence option
    }

type ReminderHistory =
    {
        CaseId: CaseId
        Revision: Revision
        State: ReminderState
        Snapshot: Reminder option
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
        MissingPayloadAt: Sequence option
        PresentPayloadAt: Sequence option
    }

/// Causal cross-entity reconstruction includes roots, structural erased identities and fixed pairs.
module internal WorkFold =
    let private evidence (entry: StoredEntry) missing present =
        (if entry.Payload.IsNone then
             missing |> Option.orElse (Some entry.Sequence)
         else
             missing),
        (if entry.Payload.IsSome then
             present |> Option.orElse (Some entry.Sequence)
         else
             present)

    let apply
        (cases: Map<CaseId, CaseHistory>)
        (actors: Map<ActorId, ActorAuthority>)
        (work: Map<CaseId, WorkHistory>)
        (reminders: Map<ReminderId, ReminderHistory>)
        (entry: StoredEntry)
        payload
        =
        let at = Some entry.Sequence

        let check value =
            require at VerificationFinding.StateSequence value

        let known value =
            known at VerificationFinding.StateSequence value

        let eligible caseId actor =
            actors
            |> Map.tryFind actor
            |> Option.exists (HistoricalCaseEffects.workEligible caseId)

        let parent caseId =
            result {
                let! parent = Map.tryFind caseId cases |> known
                do! check (parent.State = CaseState.Active())
                do! check (eligible caseId entry.ActorId)
                do! AuthorityFold.executor actors entry true false
                return parent
            }

        result {
            match entry.Subject with
            | EntryTarget.Case(id, _, CaseActKind.Open, _) ->
                do! check (not (Map.containsKey id work))

                let root =
                    {
                        CaseWorkSnapshot.CaseId = id
                        Revision = Revision.zero
                        Work = CaseWork.empty
                    }

                return
                    Map.add
                        id
                        {
                            Revision = Revision.zero
                            Snapshot = Some root
                            ChangedAt = None
                            RecordedBy = None
                            MissingPayloadAt = None
                            PresentPayloadAt = None
                        }
                        work,
                    reminders
            | EntryTarget.Case(id, _, CaseActKind.Erase, _) ->
                let work =
                    work |> Map.change id (Option.map (fun value -> { value with Snapshot = None }))

                let reminders =
                    reminders
                    |> Map.map (fun _ value ->
                        if value.CaseId = id then
                            { value with Snapshot = None }
                        else
                            value)

                return work, reminders
            | EntryTarget.Work subject ->
                let! parent = parent subject.CaseId
                let! previous = Map.tryFind subject.CaseId work |> known

                do!
                    check (
                        Revision.value previous.Revision < Revision.Maximum
                        && subject.Revision = Revision.successor previous.Revision
                    )

                let! snapshot =
                    match payload with
                    | VerifiedPayload.Work(request, actual) ->
                        result {
                            let! before = previous.Snapshot |> known

                            let! expected =
                                HistoricalWorkEffects.work request before
                                |> Result.mapError (fun _ ->
                                    divergence at VerificationFinding.StateSequence)

                            do! check (actual = expected)

                            let responsibilityChanged =
                                before.Work.Responsible <> actual.Work.Responsible

                            do!
                                check (
                                    not responsibilityChanged || parent.Status = CaseStatus.Opened
                                )

                            do!
                                check (
                                    match request.Update.Responsible with
                                    | WorkValue.Set id -> eligible subject.CaseId id
                                    | _ -> true
                                )

                            return Some actual
                        }
                    | VerifiedPayload.Erased -> Ok None
                    | _ -> Error(divergence at VerificationFinding.PayloadFormat)

                let missing, present =
                    evidence entry previous.MissingPayloadAt previous.PresentPayloadAt

                return
                    Map.add
                        subject.CaseId
                        {
                            Revision = subject.Revision
                            Snapshot = snapshot
                            ChangedAt = Some entry.RecordedAt
                            RecordedBy = Some entry.ActorId
                            MissingPayloadAt = missing
                            PresentPayloadAt = present
                        }
                        work,
                    reminders
            | EntryTarget.Reminder subject ->
                let! parent = parent subject.CaseId
                let creating = subject.Act = ReminderActKind.Create

                let! previous =
                    if creating then
                        check (not (Map.containsKey subject.ReminderId reminders))
                        |> Result.map (fun () -> None)
                    else
                        Map.tryFind subject.ReminderId reminders |> known |> Result.map Some

                do! check (entry.CreationReference.IsNone)

                do!
                    check (
                        previous
                        |> Option.forall (fun value ->
                            Revision.value value.Revision < Revision.Maximum)
                    )

                do!
                    check (
                        subject.Revision =
                            (previous
                             |> Option.map (_.Revision >> Revision.successor)
                             |> Option.defaultValue Revision.first)
                    )

                do! check (previous |> Option.forall (fun value -> value.CaseId = subject.CaseId))

                let terminalCleanup =
                    subject.Act = ReminderActKind.Complete || subject.Act = ReminderActKind.Cancel

                do! check (terminalCleanup || parent.Status = CaseStatus.Opened)

                let! state =
                    match subject.Act, previous with
                    | ReminderActKind.Create, None -> Ok ReminderState.Pending
                    | (ReminderActKind.Update | ReminderActKind.CompleteAndPlanNext), Some old when
                        old.State = ReminderState.Pending
                        ->
                        Ok(
                            if subject.Act = ReminderActKind.Update then
                                ReminderState.Pending
                            else
                                ReminderState.Completed
                        )
                    | ReminderActKind.Complete, Some old when old.State = ReminderState.Pending ->
                        Ok ReminderState.Completed
                    | ReminderActKind.Cancel, Some old when old.State = ReminderState.Pending ->
                        Ok ReminderState.Cancelled
                    | ReminderActKind.Reopen, Some old when old.State <> ReminderState.Pending ->
                        Ok ReminderState.Pending
                    | _ -> Error(divergence at VerificationFinding.StateSequence)

                do!
                    check (
                        (subject.NextReminderId.IsSome) =
                            (subject.Act = ReminderActKind.CompleteAndPlanNext)
                    )

                do!
                    check (
                        not creating
                        || subject.ReminderId = ReminderId.ofOperationId entry.OperationId
                    )

                do!
                    check (
                        subject.NextReminderId
                        |> Option.forall (fun id ->
                            id = ReminderId.ofOperationId entry.OperationId
                            && id <> subject.ReminderId
                            && not (Map.containsKey id reminders))
                    )

                let! primary, next =
                    match payload with
                    | VerifiedPayload.Reminder(request, actual) ->
                        result {
                            let before = previous |> Option.bind _.Snapshot

                            let! expected =
                                HistoricalWorkEffects.reminder request before
                                |> Result.mapError (fun _ ->
                                    divergence at VerificationFinding.StateSequence)

                            do! check (expected = actual)

                            match actual with
                            | ReminderEffects.Created value
                            | ReminderEffects.Changed value -> return Some value, None
                            | ReminderEffects.CompletedAndPlanned(completed, next) ->
                                return Some completed, Some next
                        }
                    | VerifiedPayload.Erased -> Ok(None, None)
                    | _ -> Error(divergence at VerificationFinding.PayloadFormat)

                let missing, present =
                    evidence
                        entry
                        (previous |> Option.bind _.MissingPayloadAt)
                        (previous |> Option.bind _.PresentPayloadAt)

                let history =
                    {
                        CaseId = subject.CaseId
                        Revision = subject.Revision
                        State = state
                        Snapshot = primary
                        RecordedAt = entry.RecordedAt
                        RecordedBy = entry.ActorId
                        MissingPayloadAt = missing
                        PresentPayloadAt = present
                    }

                let reminders = Map.add subject.ReminderId history reminders

                let reminders =
                    match subject.NextReminderId with
                    | None -> reminders
                    | Some id ->
                        Map.add
                            id
                            { history with
                                Revision = Revision.first
                                State = ReminderState.Pending
                                Snapshot = next
                                MissingPayloadAt = (if entry.Payload.IsNone then at else None)
                                PresentPayloadAt = (if entry.Payload.IsSome then at else None)
                            }
                            reminders

                return work, reminders
            | _ -> return work, reminders
        }

    let erasure
        (cases: Map<CaseId, CaseHistory>)
        (work: Map<CaseId, WorkHistory>)
        (reminders: Map<ReminderId, ReminderHistory>)
        =
        let check parent (missing: Sequence option) (present: Sequence option) =
            let erased =
                cases
                |> Map.tryFind parent
                |> Option.exists (fun history -> history.State = CaseState.Erased)

            if erased && present.IsSome then
                Error(divergence present VerificationFinding.ErasureIncomplete)
            elif not erased && missing.IsSome then
                Error(divergence missing VerificationFinding.PayloadWithoutErasure)
            else
                Ok()

        let work =
            work
            |> Map.toList
            |> List.map (fun (id, value) -> check id value.MissingPayloadAt value.PresentPayloadAt)

        let reminders =
            reminders
            |> Map.values
            |> Seq.map (fun value ->
                check value.CaseId value.MissingPayloadAt value.PresentPayloadAt)
            |> Seq.toList

        work @ reminders
        |> List.fold (fun state next -> state |> Result.bind (fun () -> next)) (Ok())
