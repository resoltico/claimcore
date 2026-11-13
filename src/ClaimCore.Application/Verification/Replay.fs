namespace ClaimCore.Application

open ClaimCore.RecordFormat

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts
open ClaimCore.Application.VerificationChecks

module internal ReplayAccess =
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
                    (not ownerRequired || People.isEnabledHumanOwner actor)

            return ()
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
    let subject id (history: CaseHistory) =
        history.Reference
        |> Option.map (fun reference ->
            {
                CaseSubjectState.CaseId = id
                Reference = reference
                Revision = history.Revision
                Lifecycle = CaseState.lifecycle history.State
                Status = history.Status
                Snapshot = history.Snapshot
                Payments = history.PaymentProjections
                ChangedAt = history.ChangedAt
            })

    let apply histories (entry: StoredEffect) payload =
        match entry.Subject with
        | EntryTarget.Case(id, revision, act, businessDate) ->
            let at = Some entry.Sequence
            let previous = Map.tryFind id histories
            let state = previous |> Option.bind (subject id)

            let stamp =
                {
                    SubjectStamp.Position = entry.Position
                    OperationId = entry.OperationId
                    ActorId = entry.ActorId
                    RecordedAt = entry.RecordedAt
                }

            let structure =
                {
                    CaseSubjectStructure.Stamp = stamp
                    CaseId = id
                    Reference = entry.CreationReference
                    Revision = revision
                    Act = act
                    RemovedPayment =
                        match entry.Descriptor with
                        | EffectDescriptor.Case(_, _, _, _, Some(PaymentReference.Removed id)) ->
                            Some id
                        | _ -> None
                }

            let finding =
                function
                | RecordViolation.InvalidRevisionSequence ->
                    divergence at VerificationFinding.RevisionSequence
                | RecordViolation.InvalidHistoryStart ->
                    divergence at VerificationFinding.HistoryStart
                | _ -> divergence at VerificationFinding.StateSequence

            let applyErased () =
                CaseSubject.applyErased state structure |> Result.mapError finding

            result {
                do! CaseSubject.validateStructure state structure |> Result.mapError finding

                let! next =
                    match payload with
                    | VerifiedPayload.Case(_, None) when
                        act = CaseActKind.Erase
                        && (state |> Option.exists (fun state -> state.Snapshot.IsNone))
                        ->
                        applyErased ()
                    | VerifiedPayload.Case(request, recorded) ->
                        CaseSubject.decide businessDate state request
                        |> Result.mapError (fun _ ->
                            divergence at VerificationFinding.StateSequence)
                        |> Result.bind (fun expected ->
                            require
                                at
                                VerificationFinding.StateSequence
                                (recorded = Some expected
                                 || (expected.After.State = CaseState.Erased && recorded.IsNone))
                            |> Result.map (fun () -> CaseSubject.apply stamp state expected))
                    | VerifiedPayload.Erased -> applyErased ()
                    | _ -> Error(divergence at VerificationFinding.PayloadFormat)

                let! next = next |> known at VerificationFinding.StateSequence
                do! require at VerificationFinding.RevisionSequence (next.Revision = revision)
                let first field = previous |> Option.bind field

                let missing =
                    if StoredEffect.privateUnavailable entry then
                        first _.MissingPayloadAt |> Option.orElse at
                    else
                        first _.MissingPayloadAt

                let present =
                    if StoredEffect.privateAvailable entry then
                        first _.PresentPayloadAt |> Option.orElse at
                    else
                        first _.PresentPayloadAt

                return
                    Map.add
                        id
                        {
                            Status = next.Status
                            Revision = next.Revision
                            ChangedAt = next.ChangedAt
                            Snapshot = next.Snapshot
                            PaymentProjections = next.Payments
                            Reference = Some next.Reference
                            State = CaseState.ofLifecycle next.Lifecycle ()
                            MissingPayloadAt = missing
                            PresentPayloadAt = present
                            ErasedAt = if act = CaseActKind.Erase then at else None
                        }
                        histories
            }
        | _ -> Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

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
    let subject id erased (history: WorkHistory) : WorkSubjectState =
        {
            CaseId = id
            Revision = history.Revision
            Snapshot = history.Snapshot
            ChangedAt = history.ChangedAt
            RecordedBy = history.RecordedBy
            SummaryChangedAt = history.SummaryChangedAt
            SummaryRecordedBy = history.SummaryRecordedBy
            Erased = erased
        }

    let reminderSubject id erased (history: ReminderHistory) : ReminderSubjectState =
        {
            ReminderId = id
            CaseId = history.CaseId
            Revision = history.Revision
            Status = Some history.State
            Snapshot = history.Snapshot
            RecordedAt = history.RecordedAt
            RecordedBy = history.RecordedBy
            Erased = erased
        }

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
                do! ReplayAccess.executor actors entry true false
                return parent
            }

        result {
            match entry.Subject with
            | EntryTarget.Case(id, _, CaseActKind.Open, _) ->
                do! check (not (Map.containsKey id work))

                let initial = (CaseFamily.onCaseCreated id).Work.Value

                return
                    Map.add
                        id
                        {
                            Revision = Revision.zero
                            Snapshot = initial.Snapshot
                            ChangedAt = None
                            RecordedBy = None
                            SummaryChangedAt = None
                            SummaryRecordedBy = None
                            MissingPayloadAt = None
                            PresentPayloadAt = None
                        }
                        work,
                    reminders
            | EntryTarget.Work descriptor ->
                let! parent = parent descriptor.CaseId
                let! parentState = CaseReplay.subject descriptor.CaseId parent |> known
                let! previous = Map.tryFind descriptor.CaseId work |> known
                let before = subject descriptor.CaseId false previous

                let stamp =
                    {
                        SubjectStamp.Position = entry.Position
                        OperationId = entry.OperationId
                        ActorId = entry.ActorId
                        RecordedAt = entry.RecordedAt
                    }

                let! applied =
                    match payload with
                    | VerifiedPayload.Work(request, actual) ->
                        result {
                            let! expected =
                                WorkSubject.decide
                                    {
                                        Parent = parentState
                                        AssigneeEligible = eligible descriptor.CaseId
                                    }
                                    (Some before)
                                    {
                                        ExpectedRevision = request.ExpectedRevision
                                        Update = request.Update
                                    }
                                |> Result.mapError (fun _ ->
                                    divergence at VerificationFinding.StateSequence)

                            do! check (expected = actual)

                            return
                                WorkSubject.apply
                                    entry.RecordedAt
                                    entry.ActorId
                                    (Some before)
                                    expected
                        }
                    | VerifiedPayload.Erased ->
                        WorkSubject.applyErased stamp parentState descriptor.Revision (Some before)
                        |> Result.mapError (fun _ ->
                            divergence at VerificationFinding.StateSequence)
                    | _ -> Error(divergence at VerificationFinding.PayloadFormat)

                let! applied = applied |> known
                do! check (applied.Revision = descriptor.Revision)

                let missing, present =
                    evidence entry previous.MissingPayloadAt previous.PresentPayloadAt

                return
                    Map.add
                        descriptor.CaseId
                        {
                            Revision = applied.Revision
                            Snapshot = applied.Snapshot
                            ChangedAt = applied.ChangedAt
                            RecordedBy = applied.RecordedBy
                            SummaryChangedAt = applied.SummaryChangedAt
                            SummaryRecordedBy = applied.SummaryRecordedBy
                            MissingPayloadAt = missing
                            PresentPayloadAt = present
                        }
                        work,
                    reminders
            | EntryTarget.Reminder descriptor ->
                let! parent = parent descriptor.CaseId
                let! parentState = CaseReplay.subject descriptor.CaseId parent |> known
                let previous = Map.tryFind descriptor.ReminderId reminders
                let before = previous |> Option.map (reminderSubject descriptor.ReminderId false)

                let stamp =
                    {
                        SubjectStamp.Position = entry.Position
                        OperationId = entry.OperationId
                        ActorId = entry.ActorId
                        RecordedAt = entry.RecordedAt
                    }

                let dependencies =
                    {
                        ReminderSubjectDependencies.Parent = parentState
                        IdentityReserved =
                            if descriptor.Act = ReminderActKind.Create then
                                previous.IsSome
                            else
                                descriptor.NextReminderId
                                |> Option.exists (fun id -> Map.containsKey id reminders)
                    }

                let! structural =
                    ReminderSubject.applyErased
                        dependencies
                        before
                        {
                            Stamp = stamp
                            CaseId = descriptor.CaseId
                            ReminderId = descriptor.ReminderId
                            Revision = descriptor.Revision
                            Act = descriptor.Act
                            NextReminderId = descriptor.NextReminderId
                        }
                    |> Result.mapError (fun _ -> divergence at VerificationFinding.StateSequence)

                let! applied =
                    match payload with
                    | VerifiedPayload.Reminder(request, actual) ->
                        result {
                            let! expected =
                                ReminderSubject.decide
                                    request.OperationId
                                    dependencies
                                    before
                                    request.Command
                                |> Result.mapError (fun _ ->
                                    divergence at VerificationFinding.StateSequence)

                            do! check (expected = actual)

                            let applied =
                                ReminderSubject.apply entry.RecordedAt entry.ActorId before expected

                            let shape (state: ReminderSubjectState) =
                                state.ReminderId, state.CaseId, state.Revision, state.Status

                            do! check (List.map shape applied = List.map shape structural)
                            return applied
                        }
                    | VerifiedPayload.Erased -> Ok structural
                    | _ -> Error(divergence at VerificationFinding.PayloadFormat)

                let reminders =
                    applied
                    |> List.fold
                        (fun reminders (state: ReminderSubjectState) ->
                            let previous = Map.tryFind state.ReminderId reminders

                            let missing, present =
                                evidence
                                    entry
                                    (previous |> Option.bind _.MissingPayloadAt)
                                    (previous |> Option.bind _.PresentPayloadAt)

                            let history =
                                {
                                    CaseId = state.CaseId
                                    Revision = state.Revision
                                    State = state.Status.Value
                                    Snapshot = state.Snapshot
                                    RecordedAt = state.RecordedAt
                                    RecordedBy = state.RecordedBy
                                    MissingPayloadAt = missing
                                    PresentPayloadAt = present
                                }

                            Map.add state.ReminderId history reminders)
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
    let subject id erased (history: NoteHistory) : NoteSubjectState =
        {
            NoteId = id
            CaseId = history.CaseId
            Removed = Some history.Removed
            Snapshot = history.Snapshot
            Created =
                {
                    Position = history.CreatedPosition
                    OperationId = OperationId.create (NoteId.value id) |> Option.get
                    ActorId = history.RecordedBy
                    RecordedAt = history.RecordedAt
                }
            Erased = erased
        }

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
            | EntryTarget.Note descriptor ->
                let! parent = Map.tryFind descriptor.CaseId cases |> known
                let! parentState = CaseReplay.subject descriptor.CaseId parent |> known
                do! ReplayAccess.executor actors entry true false
                let! actor = Map.tryFind entry.ActorId actors |> known

                do!
                    check (
                        CaseWorkPolicy.authorize
                            actor.Principal
                            (AccessView.ofActor (Some actor))
                            descriptor.CaseId
                        |> Result.isOk
                    )

                let prior = Map.tryFind descriptor.NoteId notes
                let before = prior |> Option.map (subject descriptor.NoteId false)

                let stamp =
                    {
                        SubjectStamp.Position = entry.Position
                        OperationId = entry.OperationId
                        ActorId = entry.ActorId
                        RecordedAt = entry.RecordedAt
                    }

                let structure =
                    {
                        NoteSubjectStructure.Stamp = stamp
                        CaseId = descriptor.CaseId
                        NoteId = descriptor.NoteId
                        Act = descriptor.Act
                    }

                let! structural =
                    NoteSubject.applyErased parentState before structure
                    |> Result.mapError (fun _ -> divergence at VerificationFinding.StateSequence)

                let! applied =
                    match payload with
                    | VerifiedPayload.Note(request, actual) ->
                        result {
                            let! expected =
                                NoteSubject.decide
                                    entry.OperationId
                                    parentState
                                    before
                                    request.Command
                                |> Result.mapError (fun _ ->
                                    divergence at VerificationFinding.StateSequence)

                            do! check (expected = actual)
                            let applied = NoteSubject.apply stamp before expected

                            let shape (state: NoteSubjectState) =
                                state.NoteId, state.CaseId, state.Removed

                            do! check (Option.map shape applied = Option.map shape structural)
                            return applied
                        }
                    | VerifiedPayload.Erased -> Ok structural
                    | _ -> Error(divergence at VerificationFinding.PayloadFormat)

                let! applied = applied |> known

                let history =
                    {
                        CaseId = applied.CaseId
                        Removed = applied.Removed.Value
                        Snapshot = applied.Snapshot
                        CreatedPosition = applied.Created.Position
                        RecordedAt = applied.Created.RecordedAt
                        RecordedBy = applied.Created.ActorId
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

                return Map.add descriptor.NoteId history notes
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

/// Deterministic operation replay. Command access is fixed before any effect is applied.
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
            People: Map<ActorId, PeopleSubjectState>
            Accepted: Map<OperationId, ActorId>
            Profiles: Map<ActorId, ProfileHistory>
            References: Map<string, CaseId>
            AvailableNonces: Set<string>
            ExpectedSeen: bool
        }

        member this.Actors = this.People |> Map.map (fun _ state -> PeopleSubject.access state)

        member this.GrantSources =
            this.People
            |> Map.toList
            |> List.collect (fun (id, state) ->
                state.Grants
                |> Map.toList
                |> List.map (fun (grant, position) -> (id, grant.Role, grant.Scope), position))
            |> Map.ofList

    let private replayPeople
        (state: Scan)
        (operation: RetainedOperation)
        (scope: PeopleCommandSkeleton)
        =
        let at = Some operation.Root.Header.Sequence

        let before =
            Map.tryFind scope.TargetActorId state.People
            |> Option.orElseWith (fun () ->
                match scope.Act with
                | PeopleAct.Register _
                | PeopleAct.Admit _
                | PeopleAct.ProvisionOwner _ ->
                    state.People
                    |> Map.values
                    |> Seq.tryFind (fun state -> state.Actor.Principal = scope.Principal)
                | _ -> None)

        let actors = state.Actors

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

        let dependencies =
            {
                PeopleSubjectDependencies.Requester =
                    AccessView.ofActor (Map.tryFind operation.ActorId actors)
                TargetCases =
                    grants
                    |> List.choose (fun grant ->
                        match grant.Scope with
                        | Scope.Case id when Map.containsKey id state.Cases ->
                            Some(GrantTarget.CaseId id, id)
                        | _ -> None)
                    |> Map.ofList
                EnabledHumanOwners =
                    Some(
                        actors |> Map.values |> Seq.filter People.isEnabledHumanOwner |> Seq.length
                    )
                ActorCount = Some actors.Count
            }

        let provenance = SubjectProvenance.people operation

        let profile =
            match operation.Values with
            | RetainedOperationValues.People(RetainedProfile.Available value) -> Some value
            | _ -> None

        let command =
            PeopleAct.traverse
                (authored >> Ok)
                (fun _ -> profile |> Option.map Ok |> Option.defaultValue (Error()))
                scope.Act

        result {
            let! after =
                match command with
                | Ok act ->
                    People.restoreCommand operation.OperationId scope.Principal act
                    |> Result.bind (
                        PeopleSubject.decide PeopleAdmissionMode.Recorded dependencies before
                    )
                    |> Result.mapError (function
                        | Refusal.People PeopleRefusal.HumanPrincipalRequired ->
                            divergence at VerificationFinding.HumanRequired
                        | _ -> divergence at VerificationFinding.PeopleFold)
                    |> Result.bind (fun effect ->
                        PeopleSubject.apply provenance before effect
                        |> Result.mapError (fun _ -> divergence at VerificationFinding.PeopleFold))
                | Error() ->
                    PeopleSubject.applyErased
                        dependencies
                        provenance
                        before
                        scope.TargetActorId
                        operation.OperationId
                        scope.Principal
                        (PeopleAct.map authored (fun _ -> None) scope.Act)
                    |> Result.mapError (fun _ -> divergence at VerificationFinding.PeopleFold)

            let! after = after |> known at VerificationFinding.PeopleFold

            do!
                require
                    at
                    VerificationFinding.PeopleFold
                    (after.Actor.ActorId = scope.TargetActorId)

            let next = Map.add scope.TargetActorId after state.People

            do!
                require
                    at
                    VerificationFinding.PeopleFold
                    (next
                     |> Map.values
                     |> Seq.exists (PeopleSubject.access >> People.isEnabledHumanOwner))

            return next
        }

    let private clearFamily (state: Scan) (parent: CaseSubjectState) =
        let id = parent.CaseId

        let family =
            {
                CaseFamilyState.Work =
                    state.Work |> Map.tryFind id |> Option.map (WorkReplay.subject id false)
                Reminders =
                    state.Reminders
                    |> Map.filter (fun _ history -> history.CaseId = id)
                    |> Map.map (fun id history -> WorkReplay.reminderSubject id false history)
                Notes =
                    state.Notes
                    |> Map.filter (fun _ history -> history.CaseId = id)
                    |> Map.map (fun id history -> NoteReplay.subject id false history)
            }

        CaseFamily.onCaseErased parent family
        |> Result.mapError (fun _ -> divergence None VerificationFinding.StateSequence)
        |> Result.map (fun family ->
            let work =
                state.Work
                |> Map.change
                    id
                    (Option.map (fun history ->
                        let owned = family.Work.Value

                        { history with
                            Snapshot = owned.Snapshot
                            SummaryChangedAt = owned.SummaryChangedAt
                            SummaryRecordedBy = owned.SummaryRecordedBy
                        }))

            let reminders =
                state.Reminders
                |> Map.map (fun id history ->
                    match Map.tryFind id family.Reminders with
                    | Some owned ->
                        { history with
                            Snapshot = owned.Snapshot
                        }
                    | None -> history)

            let notes =
                state.Notes
                |> Map.map (fun id history ->
                    match Map.tryFind id family.Notes with
                    | Some owned ->
                        { history with
                            Snapshot = owned.Snapshot
                        }
                    | None -> history)

            work, reminders, notes)

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

            do! ReplayAccess.executor actors entry human false
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
        let actors = state.Actors
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

            let! people =
                match root.Structure.Command with
                | CommandSkeleton.People scope -> replayPeople state operation scope
                | _ -> Ok state.People

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
                                            do! authorizeCase actors entry payload

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

                                let! work, reminders, notes =
                                    match entry.Subject with
                                    | EntryTarget.Case(id, _, CaseActKind.Erase, _) ->
                                        result {
                                            let! parent =
                                                Map.tryFind id cases
                                                |> Option.bind (CaseReplay.subject id)
                                                |> known at VerificationFinding.StateSequence

                                            return! clearFamily current parent
                                        }
                                    | _ ->
                                        result {
                                            let! work, reminders =
                                                if secondaryReminder then
                                                    Ok(current.Work, current.Reminders)
                                                else
                                                    WorkReplay.apply
                                                        current.Cases
                                                        actors
                                                        current.Work
                                                        current.Reminders
                                                        entry
                                                        payload

                                            let! notes =
                                                NoteReplay.apply
                                                    current.Cases
                                                    actors
                                                    current.Notes
                                                    entry
                                                    payload

                                            return work, reminders, notes
                                        }

                                return
                                    { current with
                                        Cases = cases
                                        Work = work
                                        Reminders = reminders
                                        Notes = notes
                                        References = references
                                        Profiles = profiles
                                    }
                            }))
                    (Ok state)

            let seen = expected |> Option.exists ((=) root.Position)

            return
                { next with
                    Head = root.Position
                    People = people
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
            People = Map.empty
            Accepted = Map.empty
            Profiles = Map.empty
            References = Map.empty
            AvailableNonces = Set.empty
            ExpectedSeen = expected |> Option.exists ((=) LedgerPosition.genesis)
        }

    let caseRows (state: Scan) =
        state.Cases
        |> Map.toList
        |> List.fold
            (fun rows (id, history) ->
                result {
                    let! rows = rows

                    let! subject =
                        CaseReplay.subject id history
                        |> known None VerificationFinding.CaseProjection

                    let! owned =
                        CaseSubject.rows subject |> known None VerificationFinding.CaseProjection

                    let work = state.Work |> Map.tryFind id |> Option.bind _.Snapshot

                    let pendingDue =
                        state.Reminders
                        |> Map.values
                        |> Seq.choose _.Snapshot
                        |> Seq.filter (fun value ->
                            Reminder.caseId value = id
                            && Reminder.state value = ReminderState.Pending)
                        |> Seq.map Reminder.due
                        |> Seq.toList

                    return
                        Map.add
                            id
                            (owned, CaseIndex.create owned.ChangedAt owned.Snapshot work pendingDue)
                            rows
                })
            (Ok Map.empty)

    let private projectRows finding project histories =
        histories
        |> Map.toList
        |> List.fold
            (fun rows (id, history) ->
                result {
                    let! rows = rows
                    let! row = project id history |> known None finding
                    return Map.add id row rows
                })
            (Ok Map.empty)

    let workRows (state: Scan) =
        projectRows
            VerificationFinding.CaseWorkProjection
            (fun id history ->
                WorkReplay.subject id ((Map.find id state.Cases).State = CaseState.Erased) history
                |> WorkSubject.rows)
            state.Work

    let reminderRows (state: Scan) =
        projectRows
            VerificationFinding.CaseWorkProjection
            (fun id (history: ReminderHistory) ->
                WorkReplay.reminderSubject
                    id
                    ((Map.find history.CaseId state.Cases).State = CaseState.Erased)
                    history
                |> ReminderSubject.rows)
            state.Reminders

    let noteRows (state: Scan) =
        projectRows
            VerificationFinding.CaseNotesProjection
            (fun id (history: NoteHistory) ->
                NoteReplay.subject
                    id
                    ((Map.find history.CaseId state.Cases).State = CaseState.Erased)
                    history
                |> NoteSubject.rows)
            state.Notes

    let peopleRows (state: Scan) =
        projectRows
            VerificationFinding.PeopleProjection
            (fun _ state -> PeopleSubject.rows state)
            state.People

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
