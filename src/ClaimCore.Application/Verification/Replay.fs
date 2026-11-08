namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open System
open ClaimCore.RecordFormat
open ClaimCore.Application.VerificationChecks

/// Retained authority interpretation uses only earlier ledger state, never projections or ambient authority.
module internal AuthorityReplay =

    let private ownerGrant: ActorGrant =
        {
            Role = Role.Owner
            Scope = Scope.Installation
        }

    let private owner (actor: ActorAuthority) =
        actor.Enabled
        && PrincipalKey.isHuman actor.Principal
        && List.contains ownerGrant actor.Grants

    let executor (actors: Map<ActorId, ActorAuthority>) (entry: StoredEntry) human ownerRequired =
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

    let private facts
        (actors: Map<ActorId, ActorAuthority>)
        requester
        target
        targets
        : AuthorityFacts =
        {
            TargetActor = target
            TargetCases = targets
            EnabledHumanOwners = Some(actors |> Map.values |> Seq.filter owner |> Seq.length)
            ActorCount = Some(Map.count actors)
            Requester = requester
        }

    let private requester actors (entry: StoredEntry) =
        AuthorityView.ofActor (Map.tryFind entry.ActorId actors)

    let private applyRecord findCase actors (entry: StoredEntry) (record: AuthorityEffectRecord) =
        let at = Some entry.Sequence

        let failure =
            function
            | Refusal.Authority AuthorityRefusal.HumanPrincipalRequired ->
                divergence at VerificationFinding.HumanRequired
            | _ -> divergence at VerificationFinding.AuthorityFold

        let before = Map.tryFind record.TargetActorId actors
        let view = requester actors entry

        let decided act targets =
            result {
                let! actor = before |> known at VerificationFinding.AuthorityFold

                let! command =
                    Authority.parse
                        {
                            OperationId = record.OperationId
                            Target = PrincipalKey.toInput actor.Principal
                            Act = act
                        }
                    |> Result.mapError failure

                let! change =
                    Authority.decide (facts actors view before targets) command
                    |> Result.mapError failure

                return Authority.applyChange record.TargetActorId actor.Principal before change
            }

        let registration (principal: PrincipalInput) provision =
            result {
                let! principal =
                    PrincipalKey.ofKind principal.Kind principal.Issuer principal.Subject
                    |> Result.mapError failure

                do!
                    require
                        at
                        VerificationFinding.AuthorityFold
                        (record.TargetActorId = ActorId.ofOperationId entry.OperationId
                         && not (Map.containsKey record.TargetActorId actors)
                         && (actors
                             |> Map.values
                             |> Seq.forall (fun actor -> actor.Principal <> principal))
                         && (not provision
                             || (Map.isEmpty actors
                                 && entry.Sequence = Sequence.first
                                 && entry.ActorId = record.TargetActorId)))

                let existing =
                    actors |> Map.values |> Seq.tryFind (fun actor -> actor.Principal = principal)

                do!
                    Authority.decideRegistration
                        (facts actors view existing Map.empty)
                        principal
                        provision
                    |> Result.mapError failure

                return Authority.registeredActor record.TargetActorId principal provision
            }

        let grant (value: RecordedGrant) active =
            result {
                let! target, targets =
                    match value.Scope with
                    | RecordedGrantScope.Installation -> Ok(GrantTarget.Installation, Map.empty)
                    | RecordedGrantScope.Case(id, reference) ->
                        require
                            at
                            VerificationFinding.AuthorityFold
                            (findCase (CaseReference.format reference) = Some id)
                        |> Result.map (fun () ->
                            GrantTarget.CaseId id, Map.ofList [ GrantTarget.CaseId id, id ])

                let authored =
                    {
                        AuthoredGrant.Role = value.Role
                        Scope = target
                    }

                return!
                    decided
                        (if active then
                             AuthorityAct.Grant authored
                         else
                             AuthorityAct.Revoke authored)
                        targets
            }

        result {
            let! actor =
                match record.Effect with
                | AuthorityEffect.ProvisionOwner input -> registration input true
                | AuthorityEffect.Register input -> registration input false
                | AuthorityEffect.SetProfile profile ->
                    decided (AuthorityAct.SetProfile(ActorProfile.authored profile)) Map.empty
                | AuthorityEffect.EraseProfile -> decided AuthorityAct.EraseProfile Map.empty
                | AuthorityEffect.Enable -> decided AuthorityAct.Enable Map.empty
                | AuthorityEffect.Disable -> decided AuthorityAct.Disable Map.empty
                | AuthorityEffect.Grant value -> grant value true
                | AuthorityEffect.Revoke value -> grant value false

            return Map.add record.TargetActorId actor actors
        }

    let apply _installationId findCase actors (entry: StoredEntry) (record: AuthorityEffectRecord) =
        result {
            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.PayloadFormat
                    (record.OperationId = OperationId.value entry.OperationId
                     && (match entry.Subject with
                         | EntryTarget.Authority(id, kind) ->
                             id = record.TargetActorId && kind = AuthorityEffect.kind record.Effect
                         | _ -> false))

            do!
                match record.Effect with
                | AuthorityEffect.ProvisionOwner _ -> Ok()
                | _ -> executor actors entry true true

            let! next = applyRecord findCase actors entry record

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.AuthorityFold
                    (next |> Map.values |> Seq.exists owner)

            return next
        }

    let erasedProfile actors (entry: StoredEntry) =
        result {
            do! executor actors entry true true

            let! id =
                match entry.Subject with
                | EntryTarget.Authority(id, AuthorityKind.SetProfile) -> Ok id
                | _ -> Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

            let! actor =
                Map.tryFind id actors
                |> known (Some entry.Sequence) VerificationFinding.AuthorityFold
            // The profile bytes are gone; only target/authority structure and its later erasure remain checkable.
            return
                Map.add
                    id
                    (Authority.applyChange
                        id
                        actor.Principal
                        (Some actor)
                        AuthorityChange.ProfileErased)
                    actors
        }


namespace ClaimCore.Application


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
        | VerifiedPayload.Case(_, Some snapshot) ->
            let matches =
                CaseState.map ignore snapshot.State = state
                && (reference |> Option.forall ((=) snapshot.Reference))

            require (Some sequence) VerificationFinding.StateSequence matches
            |> Result.map (fun () -> Some snapshot, Some snapshot.Reference)
        | VerifiedPayload.Case(_, None)
        | VerifiedPayload.Erased -> Ok(None, reference)
        | _ -> Error(divergence (Some sequence) VerificationFinding.PayloadFormat)

    let apply histories (entry: StoredEntry) payload =
        match entry.Subject with
        | EntryTarget.Note _
        | EntryTarget.Work _
        | EntryTarget.Reminder _
        | EntryTarget.Authority _ ->
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

                do!
                    match payload with
                    | VerifiedPayload.Case(request, snapshot) ->
                        let expected =
                            Case.decideSnapshot
                                businessDate
                                request
                                (previous |> Option.bind _.Snapshot)

                        let agrees =
                            match expected, snapshot with
                            | Ok expected, Some actual -> expected = actual
                            | Ok { State = CaseState.Erased }, None -> true
                            | _ -> false

                        require (Some entry.Sequence) VerificationFinding.StateSequence agrees
                    | _ -> Ok()

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
                    if entry.Payload.IsNone then
                        first _.MissingPayloadAt |> Option.orElse (Some entry.Sequence)
                    else
                        first _.MissingPayloadAt

                let present =
                    if entry.Payload.IsSome then
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
            |> Option.exists (
                (fun actor ->
                    Authority.can
                        actor.Principal
                        (AuthorityView.ofActor (Some actor))
                        Capability.ManageCaseWork
                        (Scope.Case caseId))
            )

        let parent caseId =
            result {
                let! parent = Map.tryFind caseId cases |> known
                do! check (parent.State = CaseState.Active())
                do! check (eligible caseId entry.ActorId)
                do! AuthorityReplay.executor actors entry true false
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


namespace ClaimCore.Application


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
        CreatedSequence: Sequence
        RecordedAt: DateTimeOffset
        RecordedBy: ActorId
        MissingPayloadAt: Sequence option
        PresentPayloadAt: Sequence option
    }

module internal NoteReplay =
    let apply
        (cases: Map<CaseId, CaseHistory>)
        (actors: Map<ActorId, ActorAuthority>)
        (notes: Map<NoteId, NoteHistory>)
        (entry: StoredEntry)
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
                do! AuthorityReplay.executor actors entry true false
                let! actor = Map.tryFind entry.ActorId actors |> known

                do!
                    check (
                        Authority.can
                            actor.Principal
                            (AuthorityView.ofActor (Some actor))
                            Capability.ReadCase
                            (Scope.Case subject.CaseId)
                        && Authority.can
                            actor.Principal
                            (AuthorityView.ofActor (Some actor))
                            Capability.ManageCaseWork
                            (Scope.Case subject.CaseId)
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
                        CreatedSequence =
                            prior
                            |> Option.map _.CreatedSequence
                            |> Option.defaultValue entry.Sequence
                        RecordedAt =
                            prior |> Option.map _.RecordedAt |> Option.defaultValue entry.RecordedAt
                        RecordedBy =
                            prior |> Option.map _.RecordedBy |> Option.defaultValue entry.ActorId
                        MissingPayloadAt =
                            if entry.Payload.IsNone then
                                prior |> Option.bind _.MissingPayloadAt |> Option.orElse at
                            else
                                prior |> Option.bind _.MissingPayloadAt
                        PresentPayloadAt =
                            if entry.Payload.IsSome then
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

/// Deterministic replay through Domain using recorded dates and as-of authority.
module Replay =
    type ProfileHistory =
        {
            Missing: Sequence option
            Present: Sequence option
        }

    type PendingEffects =
        {
            Root: StoredEntry
            NextIndex: int
            Nonces: byte array list
            Grants: Set<Role * Scope>
        }

    type Scan =
        {
            Head: LedgerPosition
            Cases: Map<CaseId, CaseHistory>
            Work: Map<CaseId, WorkHistory>
            Reminders: Map<ReminderId, ReminderHistory>
            Notes: Map<NoteId, NoteHistory>
            Actors: Map<ActorId, ActorAuthority>
            GrantSources: Map<ActorId * Role * Scope, Sequence>
            Accepted: Map<OperationId, ActorId>
            PendingEffects: PendingEffects option
            Profiles: Map<ActorId, ProfileHistory>
            References: Map<string, CaseId>
            ExpectedSeen: bool
        }

    let private effects pending (entry: StoredEntry) =
        let index = LedgerEffects.index entry.Effects
        let count = LedgerEffects.count entry.Effects

        let result =
            match pending, index with
            | None, 0 when count = 1 -> Some None
            | None, 0 ->
                Some(
                    Some
                        {
                            Root = entry
                            NextIndex = 1
                            Nonces = entry.PayloadNonce |> Option.toList
                            Grants = Set.empty
                        }
                )
            | Some pending, index when index = pending.NextIndex ->
                let root = pending.Root

                let targetMatches =
                    match root.Subject, entry.Subject with
                    | EntryTarget.Authority(target, _), EntryTarget.Authority(current, _) ->
                        target = current
                    | _ -> false

                let fresh =
                    entry.PayloadNonce
                    |> Option.forall (fun nonce -> not (List.contains nonce pending.Nonces))

                if
                    root.OperationId = entry.OperationId
                    && root.ActorId = entry.ActorId
                    && root.RecordedAt = entry.RecordedAt
                    && LedgerEffects.count root.Effects = count
                    && LedgerEffects.authorityOperation root.Effects =
                        LedgerEffects.authorityOperation entry.Effects
                    && Sequence.value entry.Sequence = Sequence.value root.Sequence + int64 index
                    && targetMatches
                    && fresh
                then
                    if index + 1 = count then
                        Some None
                    else
                        Some(
                            Some
                                { pending with
                                    NextIndex = index + 1
                                    Nonces = pending.Nonces @ (entry.PayloadNonce |> Option.toList)
                                }
                        )
                else
                    None
            | _ -> None

        result
        |> Option.map Ok
        |> Option.defaultValue (
            Error(divergence (Some entry.Sequence) VerificationFinding.OperationEffectGroup)
        )

    let private grantEffects previous pending (entry: StoredEntry) payload =
        match LedgerEffects.authorityOperation entry.Effects, payload with
        | Some AuthorityKind.Admit,
          VerifiedPayload.Authority { Effect = AuthorityEffect.Grant grant } ->
            let scope =
                match grant.Scope with
                | RecordedGrantScope.Installation -> Scope.Installation
                | RecordedGrantScope.Case(id, _) -> Scope.Case id

            let key = grant.Role, scope

            let seen =
                previous
                |> Option.map (fun (value: PendingEffects) -> value.Grants)
                |> Option.defaultValue Set.empty

            require
                (Some entry.Sequence)
                VerificationFinding.AuthorityFold
                (not (Set.contains key seen))
            |> Result.map (fun () ->
                pending
                |> Option.map (fun (value: PendingEffects) ->
                    { value with Grants = Set.add key seen }))
        | _ -> Ok pending

    let private profiles history (entry: StoredEntry) payload =
        match entry.Subject with
        | EntryTarget.Authority(id, AuthorityKind.SetProfile) ->
            let current =
                Map.tryFind id history |> Option.defaultValue { Missing = None; Present = None }

            let next =
                match payload with
                | VerifiedPayload.ErasedProfile ->
                    { current with
                        Missing = Option.orElse (Some entry.Sequence) current.Missing
                    }
                | _ ->
                    { current with
                        Present = Option.orElse (Some entry.Sequence) current.Present
                    }

            Ok(Map.add id next history)
        | EntryTarget.Authority(id, AuthorityKind.EraseProfile) ->
            match Map.tryFind id history |> Option.bind _.Present with
            | Some position ->
                Error(divergence (Some position) VerificationFinding.ErasureIncomplete)
            | None -> Ok(Map.remove id history)
        | _ -> Ok history

    let private grantSources before after sources (entry: StoredEntry) =
        match entry.Subject with
        | EntryTarget.Authority(id, _) ->
            let grants actors =
                Map.tryFind id actors
                |> Option.map (fun (actor: ActorAuthority) -> Set.ofList actor.Grants)
                |> Option.defaultValue Set.empty

            let previous = grants before
            let current = grants after

            let removed =
                Set.difference previous current
                |> Set.fold
                    (fun sources grant -> Map.remove (id, grant.Role, grant.Scope) sources)
                    sources

            Set.difference current previous
            |> Set.fold
                (fun sources grant -> Map.add (id, grant.Role, grant.Scope) entry.Sequence sources)
                removed
        | _ -> sources

    let private histories installationId state (entry: StoredEntry) payload =
        match entry.Subject, payload with
        | EntryTarget.Authority _, VerifiedPayload.ErasedProfile ->
            AuthorityReplay.erasedProfile state.Actors entry
            |> Result.map (fun actors -> state.Cases, actors)
        | EntryTarget.Authority _, VerifiedPayload.Authority request ->
            AuthorityReplay.apply
                installationId
                (fun reference -> Map.tryFind (CaseReference.key reference) state.References)
                state.Actors
                entry
                request
            |> Result.map (fun actors -> state.Cases, actors)
        | EntryTarget.Case(_, _, act, _), _ ->
            result {
                let! cases = CaseReplay.apply state.Cases entry payload

                let human =
                    match act with
                    | CaseActKind.Void
                    | CaseActKind.Reinstate
                    | CaseActKind.Erase -> true
                    | _ -> false

                do! AuthorityReplay.executor state.Actors entry human false

                do!
                    match payload, entry.Subject with
                    | VerifiedPayload.Case(request, _), EntryTarget.Case(id, _, _, _) ->
                        result {
                            let! actor =
                                Map.tryFind entry.ActorId state.Actors
                                |> known (Some entry.Sequence) VerificationFinding.ActorNotEnabled

                            let scope =
                                if act = CaseActKind.Open then
                                    Scope.Installation
                                else
                                    Scope.Case id

                            do!
                                require
                                    (Some entry.Sequence)
                                    VerificationFinding.AuthorityFold
                                    (Authority.canAct
                                        actor.Principal
                                        (AuthorityView.ofActor (Some actor))
                                        scope
                                        request.Act)
                        }
                    | _ -> Ok()

                return cases, state.Actors
            }
        | (EntryTarget.Work _ | EntryTarget.Reminder _ | EntryTarget.Note _), _ ->
            Ok(state.Cases, state.Actors)
        | _ -> Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

    let link (state: Scan) sequence previous =
        require
            (Some sequence)
            VerificationFinding.Link
            (Sequence.value state.Head.Sequence < Sequence.Maximum
             && sequence = Sequence.successor state.Head.Sequence
             && previous = EntryHash.toBytes state.Head.EntryHash)

    let append
        installationId
        (expected: LedgerPosition option)
        (state: Scan)
        (entry: StoredEntry)
        (metadata: LedgerMetadata)
        =
        result {
            let sequence = entry.Sequence
            let at = Some sequence

            let subject =
                match entry.Subject with
                | EntryTarget.Work subject -> RecordedTarget.Work subject
                | EntryTarget.Reminder subject -> RecordedTarget.Reminder subject
                | EntryTarget.Note subject -> RecordedTarget.Note subject
                | EntryTarget.Authority(id, act) ->
                    RecordedTarget.Authority { TargetActorId = id; Act = act }
                | EntryTarget.Case(id, revision, act, date) ->
                    RecordedTarget.Case
                        {
                            CaseId = id
                            Revision = revision
                            Act = act
                            BusinessDate = date
                        }

            do!
                require
                    at
                    VerificationFinding.PayloadFormat
                    (metadata.Effects = entry.Effects
                     && metadata.InstallationId = installationId
                     && metadata.Sequence = entry.Sequence
                     && metadata.OperationId = entry.OperationId
                     && metadata.ActorId = entry.ActorId
                     && metadata.RecordedAt = entry.RecordedAt
                     && metadata.Subject = subject
                     && metadata.CreationReference = entry.CreationReference
                     && metadata.PayloadCommitment = entry.PayloadCommitment)

            let! pendingEffects = effects state.PendingEffects entry

            do! link state sequence metadata.PreviousHash

            let! hash =
                capture at VerificationFinding.PayloadFormat (fun () -> LedgerEntry.hash metadata)

            do! require at VerificationFinding.EntryHash (hash = entry.EntryHash)
            let! payload = VerificationPayload.read installationId entry
            let! pendingEffects = grantEffects state.PendingEffects pendingEffects entry payload

            do!
                require
                    at
                    VerificationFinding.PayloadFormat
                    (not (Map.containsKey entry.OperationId state.Accepted))

            let! references =
                match entry.Subject, entry.CreationReference with
                | EntryTarget.Case(id, _, CaseActKind.Open, _), Some reference ->
                    let key = CaseReference.identity reference

                    require
                        at
                        VerificationFinding.HistoryStart
                        (not (Map.containsKey key state.References))
                    |> Result.map (fun () -> Map.add key id state.References)
                | EntryTarget.Case(_, _, CaseActKind.Open, _), None ->
                    Error(divergence at VerificationFinding.HistoryStart)
                | _, None -> Ok state.References
                | _, Some _ -> Error(divergence at VerificationFinding.PayloadFormat)

            let! cases, actors = histories installationId state entry payload
            let! profiles = profiles state.Profiles entry payload

            let! work, reminders =
                WorkReplay.apply state.Cases state.Actors state.Work state.Reminders entry payload

            let! notes = NoteReplay.apply state.Cases state.Actors state.Notes entry payload

            let seen =
                expected
                |> Option.exists (fun position ->
                    position.Sequence = sequence
                    && EntryHash.toBytes position.EntryHash = entry.EntryHash
                    && pendingEffects.IsNone)

            return
                {
                    Head =
                        {
                            Sequence = sequence
                            EntryHash = EntryHash.ofBytes entry.EntryHash |> Option.get
                        }
                    Work = work
                    Reminders = reminders
                    Notes = notes
                    Cases = cases
                    Actors = actors
                    GrantSources = grantSources state.Actors actors state.GrantSources entry
                    PendingEffects = pendingEffects
                    Profiles = profiles
                    Accepted =
                        if pendingEffects.IsNone then
                            Map.add entry.OperationId entry.ActorId state.Accepted
                        else
                            state.Accepted
                    References = references
                    ExpectedSeen = state.ExpectedSeen || seen
                }
        }

    let initial (expected: LedgerPosition option) =
        let head = LedgerPosition.genesis

        {
            Head = head
            Work = Map.empty
            Reminders = Map.empty
            Notes = Map.empty
            Cases = Map.empty
            Actors = Map.empty
            GrantSources = Map.empty
            Accepted = Map.empty
            PendingEffects = None
            Profiles = Map.empty
            References = Map.empty
            ExpectedSeen =
                expected
                |> Option.exists (fun position ->
                    position.Sequence = Sequence.zero && position.EntryHash = head.EntryHash)
        }

    let head (state: Scan) (actual: LedgerPosition option) =
        require None VerificationFinding.Head (actual = Some state.Head)

    let complete (expected: LedgerPosition option) (state: Scan) =
        if state.PendingEffects.IsSome then
            Error(divergence (Some state.Head.Sequence) VerificationFinding.OperationEffectGroup)
        elif expected.IsSome && not state.ExpectedSeen then
            Error(
                divergence (expected |> Option.map _.Sequence) VerificationFinding.ExpectedPosition
            )
        else
            Ok(state.Head)

    let erasure (state: Scan) =
        let missing = state.Profiles |> Map.values |> Seq.choose _.Missing |> Seq.tryHead

        match missing with
        | Some position ->
            Error(divergence (Some position) VerificationFinding.PayloadWithoutErasure)
        | None -> CaseReplay.erasure state.Cases
        |> Result.bind (fun () -> WorkReplay.erasure state.Cases state.Work state.Reminders)
        |> Result.bind (fun () -> NoteReplay.erasure state.Cases state.Notes)
