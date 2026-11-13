namespace ClaimCore.Application

open ClaimCore.RecordFormat

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts
open ClaimCore.Application.VerificationChecks

module internal PeopleReplay =
    let private ownerGrant: ActorGrant =
        {
            Role = Role.Owner
            Scope = Scope.Installation
        }

    let private owner (actor: ActorAccess) =
        actor.Enabled
        && PrincipalKey.isHuman actor.Principal
        && List.contains ownerGrant actor.Grants

    let executor (actors: Map<ActorId, ActorAccess>) (entry: StoredEffect) human ownerRequired =
        result {
            let! actor =
                Map.tryFind entry.ActorId actors
                |> known (Some entry.Sequence) VerificationFinding.ActorNotEnabled

            do! require (Some entry.Sequence) VerificationFinding.ActorNotEnabled actor.Enabled

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.HumanRequired
                    (not human || PrincipalKey.isHuman actor.Principal)

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.OwnerRequired
                    (not ownerRequired || owner actor)

            return ()
        }

    let applyRoot
        (cases: Set<CaseId>)
        (actors: Map<ActorId, ActorAccess>)
        (operation: RetainedOperation)
        (scope: PeopleCommandSkeleton)
        =
        let at = Some operation.Root.Header.Sequence
        let target = Map.tryFind scope.TargetActorId actors

        let registering =
            match scope.Act with
            | PeopleAct.Register _
            | PeopleAct.Admit _
            | PeopleAct.ProvisionOwner _ -> true
            | _ -> false

        let profile =
            match operation.Values with
            | RetainedOperationValues.People(RetainedProfile.Available value) -> Some value
            | _ -> None

        let grants =
            match scope.Act with
            | PeopleAct.Admit value -> value.Grants
            | PeopleAct.Grant grant
            | PeopleAct.Revoke grant -> [ grant ]
            | _ -> []

        let authored (grant: ActorGrant) =
            {
                AuthoredGrant.Role = grant.Role
                Scope =
                    match grant.Scope with
                    | Scope.Installation -> GrantTarget.Installation
                    | Scope.Case id -> GrantTarget.CaseId id
            }

        let act = PeopleAct.map authored (fun _ -> profile) scope.Act

        let targets =
            grants
            |> List.choose (fun grant ->
                match grant.Scope with
                | Scope.Case id when Set.contains id cases -> Some(GrantTarget.CaseId id, id)
                | _ -> None)
            |> Map.ofList

        let owner (actor: ActorAccess) =
            actor.Enabled
            && PrincipalKey.isHuman actor.Principal
            && List.contains
                {
                    Role = Role.Owner
                    Scope = Scope.Installation
                }
                actor.Grants

        let facts =
            {
                PeopleFacts.TargetActor = target
                TargetCases = targets
                Requester = AccessView.ofActor (Map.tryFind operation.ActorId actors)
                EnabledHumanOwners = Some(actors |> Map.values |> Seq.filter owner |> Seq.length)
                ActorCount = Some actors.Count
            }

        result {
            do!
                require
                    at
                    VerificationFinding.PeopleFold
                    (if registering then
                         target.IsNone
                         && (actors
                             |> Map.values
                             |> Seq.forall (fun actor -> actor.Principal <> scope.Principal))
                     else
                         target |> Option.exists (fun actor -> actor.Principal = scope.Principal))

            let! after =
                People.replayRetained
                    facts
                    scope.TargetActorId
                    operation.OperationId
                    scope.Principal
                    act
                |> Result.mapError (function
                    | Refusal.People PeopleRefusal.HumanPrincipalRequired ->
                        divergence at VerificationFinding.HumanRequired
                    | _ -> divergence at VerificationFinding.PeopleFold)

            let next = Map.add scope.TargetActorId after actors

            do! require at VerificationFinding.PeopleFold (next |> Map.values |> Seq.exists owner)

            return next
        }


namespace ClaimCore.Application

open ClaimCore.RecordFormat


open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application.VerificationChecks

type CaseHistory =
    {
        Status: CaseStatus
        Revision: Revision
        ChangedAt: DateTimeOffset
        Snapshot: CaseSnapshot option
        PaymentProjections: Map<OperationId, PaymentProjectionRow>
        Reference: CaseReference option
        State: CaseState<unit>
        MissingPayloadAt: Sequence option
        PresentPayloadAt: Sequence option
        ErasedAt: Sequence option
    }

module internal CaseReplay =
    let private nextState sequence previous act =
        let before =
            previous
            |> Option.map (_.State >> CaseState.lifecycle)
            |> Option.defaultValue Lifecycle.Active

        Lifecycle.after before act
        |> Option.map (fun state -> CaseState.ofLifecycle state ())
        |> function
            | Some state -> Ok state
            | None -> Error(divergence (Some sequence) VerificationFinding.StateSequence)

    let private snapshot sequence state reference payload =
        match payload with
        | VerifiedPayload.Case(_, Some decision) ->
            let snapshot = decision.After

            let matches =
                CaseState.map ignore snapshot.State = state
                && (reference |> Option.forall ((=) snapshot.Reference))

            require (Some sequence) VerificationFinding.StateSequence matches
            |> Result.map (fun () -> Some snapshot, Some snapshot.Reference)
        | VerifiedPayload.Case(_, None)
        | VerifiedPayload.Erased -> Ok(None, reference)
        | _ -> Error(divergence (Some sequence) VerificationFinding.PayloadFormat)

    let apply histories (entry: StoredEffect) payload =
        match entry.Subject with
        | EntryTarget.Note _
        | EntryTarget.Work _
        | EntryTarget.Reminder _
        | EntryTarget.People _ ->
            Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)
        | EntryTarget.Case(id, revision, act, businessDate) ->
            result {
                let previous = Map.tryFind id histories

                let expected =
                    previous |> Option.map _.Revision |> Option.defaultValue Revision.zero

                do!
                    require
                        (Some entry.Sequence)
                        VerificationFinding.RevisionSequence
                        (Revision.value expected < Revision.Maximum
                         && revision = Revision.successor expected)

                do!
                    require
                        (Some entry.Sequence)
                        VerificationFinding.HistoryStart
                        ((previous.IsNone) = (act = CaseActKind.Open))

                let entries =
                    previous |> Option.map _.PaymentProjections |> Option.defaultValue Map.empty

                let active =
                    entries
                    |> Map.toList
                    |> List.choose (fun (id, row) ->
                        match row.Content with
                        | PaymentProjectionContent.Available(entry, false) -> Some(id, entry)
                        | _ -> None)
                    |> Map.ofList

                let cleared (entries: Map<OperationId, PaymentProjectionRow>) =
                    entries
                    |> Map.map (fun _ row ->
                        { row with
                            Content =
                                match row.Content with
                                | PaymentProjectionContent.Available(_, removed)
                                | PaymentProjectionContent.Cleared removed ->
                                    PaymentProjectionContent.Cleared removed
                        })

                let accepting (content: PaymentProjectionContent) : PaymentProjectionRow =
                    {
                        CaseId = id
                        PaymentId = entry.OperationId
                        Position = EffectPosition.create entry.Sequence entry.Index |> Option.get
                        Content = content
                    }

                let! payments =
                    match payload with
                    | VerifiedPayload.Case(_, None) when
                        act = CaseActKind.Erase
                        && previous |> Option.exists (fun value -> value.Snapshot.IsNone)
                        ->
                        Ok(cleared entries)
                    | VerifiedPayload.Case(request, recorded) ->
                        let before = previous |> Option.bind _.Snapshot

                        let context =
                            match request.Act, before with
                            | CaseAct.RecordPayment _, Some parent ->
                                CasePaymentContext.Recording(
                                    id,
                                    parent.Revision,
                                    Map.containsKey request.OperationId entries
                                )
                            | CaseAct.RemovePayment selected, Some parent ->
                                let found = Map.tryFind selected active
                                let remaining = Map.remove selected active

                                let dates =
                                    if found.IsNone || remaining.IsEmpty then
                                        None
                                    else
                                        let dates =
                                            remaining
                                            |> Map.values
                                            |> Seq.map PaymentEntry.date
                                            |> Seq.toList

                                        PaymentDateBounds.create (List.min dates) (List.max dates)

                                CasePaymentContext.Removing(id, parent.Revision, found, dates)
                            | _ -> CasePaymentContext.NoneRequired

                        match Case.decideSnapshot businessDate context request before with
                        | Ok expected when
                            (recorded = Some expected
                             || (expected.After.State = CaseState.Erased && recorded.IsNone))
                            ->
                            match expected.After.State, expected.Payment with
                            | CaseState.Erased, _ -> Ok(cleared entries)
                            | _, Some(PaymentDifference.Recorded payment) ->
                                Ok(
                                    Map.add
                                        (PaymentEntry.id payment)
                                        (accepting (
                                            PaymentProjectionContent.Available(payment, false)
                                        ))
                                        entries
                                )
                            | _, Some(PaymentDifference.Removed payment) ->
                                let origin = Map.find (PaymentEntry.id payment) entries

                                Ok(
                                    Map.add
                                        (PaymentEntry.id payment)
                                        { origin with
                                            Content =
                                                PaymentProjectionContent.Available(payment, true)
                                        }
                                        entries
                                )
                            | _ -> Ok entries
                        | _ ->
                            Error(
                                divergence (Some entry.Sequence) VerificationFinding.StateSequence
                            )
                    | VerifiedPayload.Erased when act = CaseActKind.RecordPayment ->
                        Ok(
                            Map.add
                                entry.OperationId
                                (accepting (PaymentProjectionContent.Cleared false))
                                entries
                        )
                    | VerifiedPayload.Erased when act = CaseActKind.RemovePayment ->
                        match entry.Descriptor with
                        | EffectDescriptor.Case(_, _, _, _, Some(PaymentReference.Removed selected)) ->
                            match Map.tryFind selected entries with
                            | Some row ->
                                match row.Content with
                                | PaymentProjectionContent.Cleared false ->
                                    Ok(
                                        Map.add
                                            selected
                                            { row with
                                                Content = PaymentProjectionContent.Cleared true
                                            }
                                            entries
                                    )
                                | PaymentProjectionContent.Available(value, false) ->
                                    Ok(
                                        Map.add
                                            selected
                                            { row with
                                                Content =
                                                    PaymentProjectionContent.Available(value, true)
                                            }
                                            entries
                                    )
                                | _ ->
                                    Error(
                                        divergence
                                            (Some entry.Sequence)
                                            VerificationFinding.StateSequence
                                    )
                            | None ->
                                Error(
                                    divergence
                                        (Some entry.Sequence)
                                        VerificationFinding.StateSequence
                                )
                        | _ ->
                            Error(
                                divergence (Some entry.Sequence) VerificationFinding.PayloadFormat
                            )
                    | VerifiedPayload.Erased when act = CaseActKind.Erase -> Ok(cleared entries)
                    | VerifiedPayload.Erased -> Ok entries
                    | _ -> Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

                let! state = nextState entry.Sequence previous act

                let reference =
                    if act = CaseActKind.Open then
                        entry.CreationReference
                    else
                        previous |> Option.bind _.Reference

                do!
                    require
                        (Some entry.Sequence)
                        VerificationFinding.HistoryStart
                        (reference.IsSome
                         && (if act = CaseActKind.Open then
                                 id = CaseId.ofOperationId entry.OperationId
                             else
                                 entry.CreationReference.IsNone))

                let! snapshot, reference = snapshot entry.Sequence state reference payload
                let first field = previous |> Option.bind field

                let missing =
                    if StoredEffect.privateUnavailable entry then
                        first _.MissingPayloadAt |> Option.orElse (Some entry.Sequence)
                    else
                        first _.MissingPayloadAt

                let present =
                    if StoredEffect.privateAvailable entry then
                        first _.PresentPayloadAt |> Option.orElse (Some entry.Sequence)
                    else
                        first _.PresentPayloadAt

                return
                    Map.add
                        id
                        {
                            Status =
                                match act with
                                | CaseActKind.Open
                                | CaseActKind.Reopen -> CaseStatus.Opened
                                | CaseActKind.Close -> CaseStatus.Closed
                                | _ ->
                                    previous
                                    |> Option.map _.Status
                                    |> Option.defaultValue CaseStatus.Opened
                            Revision = revision
                            ChangedAt = entry.RecordedAt
                            Snapshot = snapshot
                            PaymentProjections = payments
                            Reference = reference
                            State = state
                            MissingPayloadAt = missing
                            PresentPayloadAt = present
                            ErasedAt =
                                if act = CaseActKind.Erase then
                                    Some entry.Sequence
                                else
                                    None
                        }
                        histories
            }

    let erasure histories =
        histories
        |> Map.values
        |> Seq.choose (fun history ->
            if history.State = CaseState.Erased && history.PresentPayloadAt.IsSome then
                Some(divergence history.ErasedAt VerificationFinding.ErasureIncomplete)
            elif history.State <> CaseState.Erased && history.MissingPayloadAt.IsSome then
                Some(divergence history.MissingPayloadAt VerificationFinding.PayloadWithoutErasure)
            else
                None)
        |> Seq.sortBy _.Sequence
        |> Seq.tryHead
        |> function
            | Some finding -> Error finding
            | None -> Ok()

    let matchesProjection _installationId histories (record: StoredCase) =
        match Map.tryFind record.CaseId histories with
        | None -> false
        | Some history ->
            let identity =
                record.Snapshot.CaseId = record.CaseId
                && history.Reference = Some record.Snapshot.Reference

            let state =
                match record.Snapshot.State with
                | CaseState.Erased ->
                    history.State = CaseState.Erased
                    && history.MissingPayloadAt.IsSome
                    && Array.isEmpty record.SnapshotBytes
                | CaseState.Active _
                | CaseState.Voided _ ->
                    history.State <> CaseState.Erased
                    && history.MissingPayloadAt.IsNone
                    && history.Snapshot = Some record.Snapshot
                    && (history.Snapshot
                        |> Option.exists (fun snapshot ->
                            ClaimCore.RecordFormat.CaseRecord.encodeSnapshot snapshot =
                                record.SnapshotBytes))
                    && CaseState.map ignore record.Snapshot.State = history.State

            identity && record.Snapshot.Revision = history.Revision && state


namespace ClaimCore.Application

open ClaimCore.RecordFormat

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
        SummaryChangedAt: DateTimeOffset option
        SummaryRecordedBy: ActorId option
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
module internal WorkReplay =
    let private evidence (entry: StoredEffect) missing present =
        (if StoredEffect.privateUnavailable entry then
             missing |> Option.orElse (Some entry.Sequence)
         else
             missing),
        (if StoredEffect.privateAvailable entry then
             present |> Option.orElse (Some entry.Sequence)
         else
             present)

    let apply
        (cases: Map<CaseId, CaseHistory>)
        (actors: Map<ActorId, ActorAccess>)
        (work: Map<CaseId, WorkHistory>)
        (reminders: Map<ReminderId, ReminderHistory>)
        (entry: StoredEffect)
        payload
        =
        let at = Some entry.Sequence

        let check value =
            require at VerificationFinding.StateSequence value

        let known value =
            known at VerificationFinding.StateSequence value

        let policy caseId actorId decide =
            actors
            |> Map.tryFind actorId
            |> Option.exists (fun actor ->
                decide actor.Principal (AccessView.ofActor (Some actor)) caseId)

        let authorized caseId actorId =
            policy caseId actorId (fun principal people caseId ->
                CaseWorkPolicy.authorize principal people caseId |> Result.isOk)

        let eligible caseId actorId =
            policy caseId actorId CaseWorkPolicy.eligibleAssignee

        let parent caseId =
            result {
                let! parent = Map.tryFind caseId cases |> known
                do! check (parent.State = CaseState.Active())
                do! check (authorized caseId entry.ActorId)
                do! PeopleReplay.executor actors entry true false
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
                            SummaryChangedAt = None
                            SummaryRecordedBy = None
                            MissingPayloadAt = None
                            PresentPayloadAt = None
                        }
                        work,
                    reminders
            | EntryTarget.Case(id, _, CaseActKind.Erase, _) ->
                let work =
                    work
                    |> Map.change
                        id
                        (Option.map (fun value ->
                            { value with
                                Snapshot = None
                                SummaryChangedAt = None
                                SummaryRecordedBy = None
                            }))

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
                            let! parentSnapshot = parent.Snapshot |> known

                            let! expected =
                                CaseWorkDecision.decide
                                    parentSnapshot
                                    (match request.Update.Responsible with
                                     | WorkValue.Set id -> eligible subject.CaseId id
                                     | _ -> true)
                                    request.ExpectedRevision
                                    request.Update
                                    before
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

                let summaryChangedAt, summaryRecordedBy =
                    match snapshot |> Option.bind (fun value -> value.Work.Summary) with
                    | None -> None, None
                    | Some summary when
                        previous.Snapshot |> Option.bind (fun value -> value.Work.Summary) =
                            Some summary
                        ->
                        previous.SummaryChangedAt, previous.SummaryRecordedBy
                    | Some _ -> Some entry.RecordedAt, Some entry.ActorId

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
                            SummaryChangedAt = summaryChangedAt
                            SummaryRecordedBy = summaryRecordedBy
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
                    ReminderState.after subject.Act (previous |> Option.map _.State)
                    |> function
                        | Some state -> Ok state
                        | None -> Error(divergence at VerificationFinding.StateSequence)

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
                            let! parentSnapshot = parent.Snapshot |> known

                            let! expected =
                                Reminder.decide
                                    request.OperationId
                                    parentSnapshot
                                    before
                                    (subject.NextReminderId
                                     |> Option.exists (fun id -> Map.containsKey id reminders))
                                    request.Command
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
                                State =
                                    ReminderState.after ReminderActKind.Create None |> Option.get
                                Snapshot = next
                                MissingPayloadAt =
                                    (if StoredEffect.privateUnavailable entry then at else None)
                                PresentPayloadAt =
                                    (if StoredEffect.privateAvailable entry then at else None)
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


namespace ClaimCore.Application

open ClaimCore.RecordFormat


open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application.VerificationChecks

type NoteHistory =
    {
        CaseId: CaseId
        Removed: bool
        Snapshot: Note option
        CreatedPosition: EffectPosition
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
        MissingPayloadAt: Sequence option
        PresentPayloadAt: Sequence option
    }

module internal NoteReplay =
    let apply
        (cases: Map<CaseId, CaseHistory>)
        (actors: Map<ActorId, ActorAccess>)
        (notes: Map<NoteId, NoteHistory>)
        (entry: StoredEffect)
        payload
        =
        let at = Some entry.Sequence
        let check = require at VerificationFinding.StateSequence

        let known value =
            known at VerificationFinding.StateSequence value

        result {
            match entry.Subject with
            | EntryTarget.Note subject ->
                let! parent = Map.tryFind subject.CaseId cases |> known
                do! check (parent.State = CaseState.Active())
                do! PeopleReplay.executor actors entry true false
                let! actor = Map.tryFind entry.ActorId actors |> known

                do!
                    check (
                        CaseWorkPolicy.authorize
                            actor.Principal
                            (AccessView.ofActor (Some actor))
                            subject.CaseId
                        |> Result.isOk
                    )

                let prior = Map.tryFind subject.NoteId notes
                let adding = subject.Act = NoteActKind.AddNote

                do!
                    check (
                        if adding then
                            prior.IsNone && subject.NoteId = NoteId.ofOperationId entry.OperationId
                        else
                            prior
                            |> Option.exists (fun value ->
                                value.CaseId = subject.CaseId && not value.Removed)
                    )

                let! snapshot =
                    match payload with
                    | VerifiedPayload.Note(request, actual) when
                        parent.Snapshot.IsNone && subject.Act = NoteActKind.RemoveNote
                        ->
                        check (
                            request.Command = NoteCommand.RemoveNote(subject.CaseId, subject.NoteId)
                            && Note.id actual = subject.NoteId
                            && Note.caseId actual = subject.CaseId
                            && Note.text actual = None
                        )
                        |> Result.map (fun () -> Some actual)
                    | VerifiedPayload.Note(request, actual) ->
                        result {
                            let! parent = parent.Snapshot |> known

                            let! expected =
                                Note.decide
                                    entry.OperationId
                                    parent
                                    (prior |> Option.bind _.Snapshot)
                                    request.Command
                                |> Result.mapError (fun _ ->
                                    divergence at VerificationFinding.StateSequence)

                            do! check (expected = actual)
                            return Some actual
                        }
                    | VerifiedPayload.Erased -> Ok None
                    | _ -> Error(divergence at VerificationFinding.PayloadFormat)

                let history =
                    {
                        CaseId = subject.CaseId
                        Removed = not adding
                        Snapshot = snapshot
                        CreatedPosition =
                            prior
                            |> Option.map _.CreatedPosition
                            |> Option.defaultValue entry.Position
                        RecordedAt =
                            prior |> Option.map _.RecordedAt |> Option.defaultValue entry.RecordedAt
                        RecordedBy =
                            prior |> Option.map _.RecordedBy |> Option.defaultValue entry.ActorId
                        MissingPayloadAt =
                            if StoredEffect.privateUnavailable entry then
                                prior |> Option.bind _.MissingPayloadAt |> Option.orElse at
                            else
                                prior |> Option.bind _.MissingPayloadAt
                        PresentPayloadAt =
                            if StoredEffect.privateAvailable entry then
                                prior |> Option.bind _.PresentPayloadAt |> Option.orElse at
                            else
                                prior |> Option.bind _.PresentPayloadAt
                    }

                return Map.add subject.NoteId history notes
            | _ -> return notes
        }

    let erasure (cases: Map<CaseId, CaseHistory>) (notes: Map<NoteId, NoteHistory>) =
        notes
        |> Map.values
        |> Seq.fold
            (fun result note ->
                result
                |> Result.bind (fun () ->
                    let erased =
                        cases
                        |> Map.tryFind note.CaseId
                        |> Option.exists (fun parent -> parent.State = CaseState.Erased)

                    if erased && note.PresentPayloadAt.IsSome then
                        Error(
                            divergence note.PresentPayloadAt VerificationFinding.ErasureIncomplete
                        )
                    elif not erased && note.MissingPayloadAt.IsSome then
                        Error(
                            divergence
                                note.MissingPayloadAt
                                VerificationFinding.PayloadWithoutErasure
                        )
                    else
                        Ok()))
            (Ok())

/// Deterministic operation replay. Command people is fixed before any effect is applied.
module Replay =
    type ProfileHistory =
        {
            Missing: EffectPosition option
            Present: EffectPosition option
        }

    type Scan =
        {
            Head: LedgerPosition
            Cases: Map<CaseId, CaseHistory>
            Work: Map<CaseId, WorkHistory>
            Reminders: Map<ReminderId, ReminderHistory>
            Notes: Map<NoteId, NoteHistory>
            Actors: Map<ActorId, ActorAccess>
            GrantSources: Map<ActorId * Role * Scope, EffectPosition>
            Accepted: Map<OperationId, ActorId>
            Profiles: Map<ActorId, ProfileHistory>
            References: Map<string, CaseId>
            AvailableNonces: Set<string>
            ExpectedSeen: bool
        }

    let link (state: Scan) sequence previous =
        require
            (Some sequence)
            VerificationFinding.Link
            (Sequence.value state.Head.Sequence < Sequence.Maximum
             && sequence = Sequence.successor state.Head.Sequence
             && previous = EntryHash.toBytes state.Head.EntryHash)

    let private profile history (entry: StoredEffect) =

        match entry.Descriptor with
        | EffectDescriptor.People(id, PeopleEffectDescriptor.SetProfile _) ->
            let current =
                Map.tryFind id history |> Option.defaultValue { Missing = None; Present = None }

            let next =
                match entry.Operation.Values with
                | RetainedOperationValues.People RetainedProfile.Unavailable ->
                    { current with
                        Missing = Option.orElse current.Missing (Some entry.Position)
                    }
                | RetainedOperationValues.People(RetainedProfile.Available _) ->
                    { current with
                        Present = Option.orElse current.Present (Some entry.Position)
                    }
                | _ -> current

            Ok(Map.add id next history)
        | EffectDescriptor.People(id, PeopleEffectDescriptor.EraseProfile) ->
            match Map.tryFind id history |> Option.bind _.Present with
            | Some position ->
                Error(
                    divergence
                        (Some(EffectPosition.sequence position))
                        VerificationFinding.ErasureIncomplete
                )
            | None -> Ok(Map.remove id history)
        | _ -> Ok history

    let private grantSource sources (entry: StoredEffect) =
        match entry.Descriptor with
        | EffectDescriptor.People(id, PeopleEffectDescriptor.ProvisionOwner _) ->
            Map.add (id, Role.Owner, Scope.Installation) entry.Position sources
        | EffectDescriptor.People(id, PeopleEffectDescriptor.Grant grant) ->
            Map.add (id, grant.Role, grant.Scope) entry.Position sources
        | EffectDescriptor.People(id, PeopleEffectDescriptor.Revoke grant) ->
            Map.remove (id, grant.Role, grant.Scope) sources
        | _ -> sources

    let private authorizeCase actors (entry: StoredEffect) payload =
        result {
            let! actor =
                Map.tryFind entry.ActorId actors
                |> known (Some entry.Sequence) VerificationFinding.ActorNotEnabled

            let! parent, act =
                match entry.Subject with
                | EntryTarget.Case(id, _, act, _) -> Ok(id, act)
                | _ -> Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

            let human =
                List.contains act [ CaseActKind.Void; CaseActKind.Reinstate; CaseActKind.Erase ]

            do! PeopleReplay.executor actors entry human false
            let view = AccessView.ofActor (Some actor)

            let scope =
                if act = CaseActKind.Open then
                    Scope.Installation
                else
                    Scope.Case parent

            let permitted =
                match payload with
                | VerifiedPayload.Case(command, _) ->
                    Access.canAct actor.Principal view scope command.Act
                | VerifiedPayload.Erased -> Access.canAdvertise actor.Principal view scope act
                | _ -> false

            do! require (Some entry.Sequence) VerificationFinding.PeopleFold permitted
        }

    let append
        installationId
        (expected: LedgerPosition option)
        (state: Scan)
        (operation: RetainedOperation)
        =
        let root = operation.Root
        let at = Some root.Header.Sequence

        result {
            do!
                require
                    at
                    VerificationFinding.PayloadFormat
                    (root.Header.InstallationId = installationId)

            do! link state root.Header.Sequence (EntryHash.toBytes root.Header.PreviousHash)

            do!
                require
                    at
                    VerificationFinding.PayloadFormat
                    (not (Map.containsKey operation.OperationId state.Accepted))

            let nonces =
                root.Structure.Compartments
                |> List.choose (fun value ->
                    Compartment.content value
                    |> Option.map (fun (nonce, _) -> Convert.ToHexString nonce))

            do!
                require
                    at
                    VerificationFinding.Commitment
                    ((List.distinct nonces).Length = nonces.Length
                     && nonces
                        |> List.forall (fun nonce -> not (Set.contains nonce state.AvailableNonces)))

            let! actors =
                match root.Structure.Command with
                | CommandSkeleton.People scope ->
                    PeopleReplay.applyRoot
                        (state.Cases |> Map.keys |> Set.ofSeq)
                        state.Actors
                        operation
                        scope
                | _ -> Ok state.Actors

            let! next =
                StoredEffect.ofOperation operation
                |> List.fold
                    (fun accumulated entry ->
                        accumulated
                        |> Result.bind (fun (current: Scan) ->
                            result {
                                let! payload = VerificationPayload.read installationId entry
                                let! profiles = profile current.Profiles entry

                                let! cases, references =
                                    match entry.Subject with
                                    | EntryTarget.Case(id, _, act, _) ->
                                        result {
                                            do! authorizeCase state.Actors entry payload

                                            let! cases =
                                                CaseReplay.apply current.Cases entry payload

                                            let! references =
                                                match act, entry.CreationReference with
                                                | CaseActKind.Open, Some reference ->
                                                    let key = CaseReference.identity reference

                                                    require
                                                        at
                                                        VerificationFinding.HistoryStart
                                                        (not (
                                                            Map.containsKey key current.References
                                                        ))
                                                    |> Result.map (fun () ->
                                                        Map.add key id current.References)
                                                | CaseActKind.Open, None ->
                                                    Error(
                                                        divergence
                                                            at
                                                            VerificationFinding.HistoryStart
                                                    )
                                                | _, None -> Ok current.References
                                                | _ ->
                                                    Error(
                                                        divergence
                                                            at
                                                            VerificationFinding.PayloadFormat
                                                    )

                                            return cases, references
                                        }
                                    | _ -> Ok(current.Cases, current.References)
                                // A compound reminder is independently decided once from its pre-operation
                                // state; that decision supplies both declared targets, not two commands.
                                let secondaryReminder =
                                    match root.Structure.Command with
                                    | CommandSkeleton.Reminder scope ->
                                        scope.Act = ReminderActKind.CompleteAndPlanNext
                                        && entry.Index = 1
                                    | _ -> false

                                let! work, reminders =
                                    if secondaryReminder then
                                        Ok(current.Work, current.Reminders)
                                    else
                                        WorkReplay.apply
                                            current.Cases
                                            state.Actors
                                            current.Work
                                            current.Reminders
                                            entry
                                            payload

                                let! notes =
                                    NoteReplay.apply
                                        current.Cases
                                        state.Actors
                                        current.Notes
                                        entry
                                        payload

                                return
                                    { current with
                                        Cases = cases
                                        Work = work
                                        Reminders = reminders
                                        Notes = notes
                                        References = references
                                        Profiles = profiles
                                        GrantSources = grantSource current.GrantSources entry
                                    }
                            }))
                    (Ok state)

            let seen = expected |> Option.exists ((=) root.Position)

            return
                { next with
                    Head = root.Position
                    Actors = actors
                    Accepted = Map.add operation.OperationId operation.ActorId state.Accepted
                    AvailableNonces = Set.union state.AvailableNonces (Set.ofList nonces)
                    ExpectedSeen = state.ExpectedSeen || seen
                }
        }

    let initial (expected: LedgerPosition option) =
        {
            Head = LedgerPosition.genesis
            Cases = Map.empty
            Work = Map.empty
            Reminders = Map.empty
            Notes = Map.empty
            Actors = Map.empty
            GrantSources = Map.empty
            Accepted = Map.empty
            Profiles = Map.empty
            References = Map.empty
            AvailableNonces = Set.empty
            ExpectedSeen = expected |> Option.exists ((=) LedgerPosition.genesis)
        }

    let head (state: Scan) actual =
        require None VerificationFinding.Head (actual = Some state.Head)

    let complete (expected: LedgerPosition option) (state: Scan) =
        if expected.IsSome && not state.ExpectedSeen then
            Error(
                divergence (expected |> Option.map _.Sequence) VerificationFinding.ExpectedPosition
            )
        else
            Ok state.Head

    let erasure (state: Scan) =
        let missing = state.Profiles |> Map.values |> Seq.choose _.Missing |> Seq.tryHead

        match missing with
        | Some position ->
            Error(
                divergence
                    (Some(EffectPosition.sequence position))
                    VerificationFinding.PayloadWithoutErasure
            )
        | None -> CaseReplay.erasure state.Cases
        |> Result.bind (fun () -> WorkReplay.erasure state.Cases state.Work state.Reminders)
        |> Result.bind (fun () -> NoteReplay.erasure state.Cases state.Notes)
