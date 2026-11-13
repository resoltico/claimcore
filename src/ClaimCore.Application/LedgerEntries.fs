namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

[<RequireQualifiedAccess; NoComparison>]
type internal EntryContent =
    | Case of CaseCommand * CaseDecision option
    | People of PeopleEffectDescriptor * ActorProfile option
    | Work of WorkDecisionInput * CaseWorkSnapshot
    | Reminder of ReminderDecisionInput * ReminderEffects
    | Note of NoteDecisionInput * Note
    | ErasedCase
    | ErasedProfile

[<NoComparison>]
type internal ValidatedEntry =
    private
        {
            InstallationId: Guid
            Entry: StoredEffect
            Content: EntryContent
        }

[<NoComparison>]
type internal ValidatedOperation =
    private
        {
            Stored: RetainedOperation
            ValidatedEntries: ValidatedEntry list
        }

[<NoComparison>]
type internal OperationIdentityRequest =
    {
        OperationId: OperationId
        Bytes: byte array
        Existing: RetainedOperation option
    }

module LedgerEntries =
    let internal content (value: ValidatedEntry) = value.Content

    let position sequence hash =
        EntryHash.ofBytes hash
        |> Option.map (LedgerPosition.create sequence)
        |> Option.defaultWith (fun () -> invalidArg (nameof hash) "Invalid operation position.")

    let internal primary operation =
        StoredEffect.ofOperation operation |> List.head

    let internal canObserve principal actor =
        function
        | EntryTarget.Case(id, _, _, _) ->
            Access.can principal actor Capability.ReadHistory (Scope.Case id)
        | EntryTarget.Work subject ->
            Access.can principal actor Capability.ReadHistory (Scope.Case subject.CaseId)
        | EntryTarget.Note subject ->
            Access.can principal actor Capability.ReadHistory (Scope.Case subject.CaseId)
        | EntryTarget.Reminder subject ->
            Access.can principal actor Capability.ReadHistory (Scope.Case subject.CaseId)
        | EntryTarget.People _ ->
            Access.can principal actor Capability.ReadAccess Scope.Installation

    let internal validate installation (entry: StoredEffect) =
        let operation = entry.Operation

        if operation.Root.Header.InstallationId <> installation then
            Error CoreFault.StoreIntegrityError
        else
            let available =
                match entry.Descriptor, operation.Values with
                | EffectDescriptor.Case(_, _, CaseActKind.Erase, _, _),
                  RetainedOperationValues.Case(Some value) ->
                    Ok(EntryContent.Case(value.Command, None))
                | EffectDescriptor.Case _, RetainedOperationValues.Case(Some value) ->
                    Ok(EntryContent.Case(value.Command, Some value.Decision))
                | EffectDescriptor.Case _, RetainedOperationValues.Case None ->
                    Ok EntryContent.ErasedCase
                | EffectDescriptor.People(_, PeopleEffectDescriptor.SetProfile _),
                  RetainedOperationValues.People RetainedProfile.Unavailable ->
                    Ok EntryContent.ErasedProfile
                | EffectDescriptor.People(_, change), RetainedOperationValues.People profile ->
                    let profile =
                        match profile with
                        | RetainedProfile.Available value -> Some value
                        | _ -> None

                    Ok(EntryContent.People(change, profile))
                | EffectDescriptor.Work(parent, _), RetainedOperationValues.Work(Some value) ->
                    match operation.Root.Structure.Command with
                    | CommandSkeleton.Work(_, expected) ->
                        Ok(
                            EntryContent.Work(
                                {
                                    CaseId = parent
                                    ExpectedRevision = expected
                                    Update = value.Update
                                },
                                value.After
                            )
                        )
                    | _ -> Error CoreFault.StoreIntegrityError
                | EffectDescriptor.Work _, RetainedOperationValues.Work None
                | (EffectDescriptor.NoteAdded _ | EffectDescriptor.NoteRemoved _),
                  RetainedOperationValues.Note None
                | (EffectDescriptor.ReminderCreated _ | EffectDescriptor.ReminderChanged _),
                  RetainedOperationValues.Reminder None -> Ok EntryContent.ErasedCase
                | (EffectDescriptor.NoteAdded _ | EffectDescriptor.NoteRemoved _),
                  RetainedOperationValues.Note(Some value) ->
                    Ok(
                        EntryContent.Note(
                            {
                                OperationId = entry.OperationId
                                Command = value.Command
                            },
                            value.After
                        )
                    )
                | (EffectDescriptor.ReminderCreated _ | EffectDescriptor.ReminderChanged _),
                  RetainedOperationValues.Reminder(Some value) ->
                    match value.Command, value.Effects, entry.Index with
                    | ReminderCommand.Change(parent, _, _, ReminderChange.CompleteAndPlanNext plan),
                      ReminderEffects.CompletedAndPlanned(_, next),
                      1 ->
                        Ok(
                            EntryContent.Reminder(
                                {
                                    OperationId = entry.OperationId
                                    Command = ReminderCommand.Create(parent, plan)
                                },
                                ReminderEffects.Created next
                            )
                        )
                    | _ ->
                        Ok(
                            EntryContent.Reminder(
                                {
                                    OperationId = entry.OperationId
                                    Command = value.Command
                                },
                                value.Effects
                            )
                        )
                | _ -> Error CoreFault.StoreIntegrityError

            available
            |> Result.map (fun content ->
                {
                    InstallationId = installation
                    Entry = entry
                    Content = content
                })

    let internal validateOperation installation operation =
        StoredEffect.ofOperation operation
        |> List.fold
            (fun state entry ->
                state
                |> Result.bind (fun entries ->
                    validate installation entry |> Result.map (fun value -> value :: entries)))
            (Ok [])
        |> Result.map (fun entries ->
            {
                Stored = operation
                ValidatedEntries = List.rev entries
            })

    let internal peopleCases (entry: StoredEffect) =
        match entry.Descriptor with
        | EffectDescriptor.People(_,
                                  (PeopleEffectDescriptor.Grant { Scope = Scope.Case id } | PeopleEffectDescriptor.Revoke {
                                                                                                                              Scope = Scope.Case id
                                                                                                                          })) ->
            [ id ]
        | _ -> []

    let private peopleHistoryFacts cases act =
        function
        | EntryContent.ErasedProfile when act = PeopleActKind.SetProfile ->
            Ok(PeopleHistoryFacts.ProfileSet None)
        | EntryContent.People(change, profile) ->
            let scoped (grant: ActorGrant) =
                match grant.Scope with
                | Scope.Installation ->
                    Ok
                        {
                            ScopedGrant.Role = grant.Role
                            Scope = GrantScope.Installation
                        }
                | Scope.Case id ->
                    Map.tryFind id cases
                    |> Option.map (fun reference ->
                        Ok
                            {
                                ScopedGrant.Role = grant.Role
                                Scope = GrantScope.Case(id, reference)
                            })
                    |> Option.defaultValue (Error CoreFault.StoreIntegrityError)

            match change with
            | PeopleEffectDescriptor.Register principal ->
                Ok(PeopleHistoryFacts.Registered principal)
            | PeopleEffectDescriptor.ProvisionOwner principal ->
                Ok(PeopleHistoryFacts.OwnerProvisioned principal)
            | PeopleEffectDescriptor.SetProfile _ -> Ok(PeopleHistoryFacts.ProfileSet profile)
            | PeopleEffectDescriptor.EraseProfile -> Ok PeopleHistoryFacts.ProfileErased
            | PeopleEffectDescriptor.Enable -> Ok PeopleHistoryFacts.Enabled
            | PeopleEffectDescriptor.Disable -> Ok PeopleHistoryFacts.Disabled
            | PeopleEffectDescriptor.Grant grant ->
                scoped grant |> Result.map PeopleHistoryFacts.Granted
            | PeopleEffectDescriptor.Revoke grant ->
                scoped grant |> Result.map PeopleHistoryFacts.Revoked
        | _ -> Error CoreFault.StoreIntegrityError

    let internal peopleHistory cases (actors: Map<ActorId, ActorRef>) (validated: ValidatedEntry) =
        let entry = validated.Entry

        match entry.Subject with
        | EntryTarget.People(target, act) ->
            peopleHistoryFacts cases act validated.Content
            |> Result.bind (fun facts ->
                match Map.tryFind entry.ActorId actors, Map.tryFind target actors with
                | Some author, Some targetRef ->
                    Ok
                        {
                            PeopleHistoryEntry.OperationId = entry.OperationId
                            Position = entry.Position
                            OperationHash = entry.Operation.Root.Hash
                            RecordedAt = entry.RecordedAt
                            RecordedBy = author
                            Target = targetRef
                            Facts = facts
                        }
                | _ -> Error CoreFault.StoreIntegrityError)
        | _ -> Error CoreFault.StoreIntegrityError

    let internal identityBytes (validated: ValidatedEntry) =
        OperationIntent.accepted validated.Entry.Operation

    let internal identityBytesOperation (operation: ValidatedOperation) =
        OperationIntent.accepted operation.Stored

    /// The same snapshot-disclosure policy shapes append, replay, preview, observation and history.
    let private disclosure principal actor (current: StoredCase) revision snapshot =
        let id = current.CaseId
        let currentRevision = current.Snapshot.Revision

        if
            Access.can principal actor Capability.ReadHistoryFull (Scope.Case id)
            || (currentRevision = revision
                && Access.can principal actor Capability.ReadCase (Scope.Case id))
        then
            SnapshotDisclosure.Disclosed snapshot
        else
            SnapshotDisclosure.Withheld

    /// Field selection comes from the recorded command, including an acceptance replay.
    let private recordedFields (command: CaseCommand) =
        let decision (update: ValidDecisionUpdate) =
            [
                if update.Date.IsSome then
                    BusinessField.PaymentDecisionDate
                if update.Payable.IsSome then
                    BusinessField.PayableAmount
                    BusinessField.PayableCurrency
            ]

        match command.Act with
        | CaseAct.Open _ -> BusinessField.ofGroup RecordGroup.Registration
        | CaseAct.AmendRegistration update -> RegistrationUpdate.changes update |> List.map fst
        | CaseAct.Decide _ -> BusinessField.ofGroup RecordGroup.Decision
        | CaseAct.Correct correction ->
            (match correction.Registration with
             | Amendment.Keep -> []
             | Amendment.Update update -> RegistrationUpdate.changes update |> List.map fst)
            @ (match correction.Decision with
               | Change.Keep -> []
               | Change.Clear -> BusinessField.ofGroup RecordGroup.Decision
               | Change.Update update -> decision update)
        | CaseAct.RecordPayment _
        | CaseAct.RemovePayment _ -> BusinessField.ofGroup RecordGroup.Payment
        | CaseAct.Bare BareAct.WithdrawDecision -> BusinessField.ofGroup RecordGroup.Decision
        | CaseAct.Bare(BareAct.Close | BareAct.Reopen | BareAct.Erase)
        | CaseAct.Void _
        | CaseAct.Reinstate _ -> []

    let private caseReceipt
        principal
        actor
        (current: StoredCase option)
        (validated: ValidatedEntry)
        =
        let content = validated.Content

        match validated.Entry.Subject with
        | EntryTarget.People _
        | EntryTarget.Work _
        | EntryTarget.Note _
        | EntryTarget.Reminder _ -> Error CoreFault.StoreIntegrityError
        | EntryTarget.Case(id, revision, act, _) ->
            match current with
            | None -> Error CoreFault.StoreIntegrityError
            | Some record when
                record.CaseId <> id
                || record.Snapshot.Revision < revision
                || (Case.ofAccepted record.Snapshot |> Result.isError)
                ->
                Error CoreFault.StoreIntegrityError
            | Some record when record.Snapshot.CaseId <> id -> Error CoreFault.StoreIntegrityError
            | Some record ->
                let shown =
                    match record.Snapshot.State, content with
                    | CaseState.Erased, EntryContent.ErasedCase -> Ok SnapshotDisclosure.Erased
                    | CaseState.Erased, EntryContent.Case(_, None) when act = CaseActKind.Erase ->
                        Ok SnapshotDisclosure.Erased
                    | (CaseState.Active _ | CaseState.Voided _),
                      EntryContent.Case(command, Some decision) when
                        decision.After.Reference = record.Snapshot.Reference
                        ->
                        match LiveCase.ofState decision.After.State with
                        | None -> Error CoreFault.StoreIntegrityError
                        | Some live ->
                            Ok(
                                disclosure
                                    principal
                                    actor
                                    record
                                    revision
                                    {
                                        DisclosedCaseReceipt.State = live
                                        RecordedFields = recordedFields command
                                        Payment = decision.Payment
                                    }
                            )
                    | _ -> Error CoreFault.StoreIntegrityError

                shown
                |> Result.map (fun snapshot ->
                    ReceiptSubject.Case
                        {
                            CaseId = id
                            Reference = record.Snapshot.Reference
                            Revision = revision
                            Act = act
                            Snapshot = snapshot
                        })

    let private canReadVersion principal actor parentId currentRevision revision =
        Access.can principal actor Capability.ReadHistoryFull (Scope.Case parentId)
        || (currentRevision = Some revision
            && Access.can principal actor Capability.ReadCase (Scope.Case parentId))

    /// Only names belonging to versions disclosed by this same snapshot may be requested.
    let internal workRelations
        principal
        actor
        (parent: StoredCase option)
        (current: StoredWork option)
        entries
        =
        match parent with
        | Some parent when CaseState.lifecycle parent.Snapshot.State <> Lifecycle.Erased ->
            let revision = current |> Option.map _.Revision

            entries
            |> List.choose (fun (entry: ValidatedEntry) ->
                match entry.Entry.Subject, entry.Content with
                | EntryTarget.Work metadata, EntryContent.Work(_, snapshot) when
                    metadata.CaseId = parent.CaseId
                    && canReadVersion principal actor parent.CaseId revision metadata.Revision
                    ->
                    snapshot.Work.Responsible
                    |> Option.map (fun identity -> parent.CaseId, identity)
                | _ -> None)
            |> List.distinct
        | _ -> []

    let private workReceipt
        principal
        actor
        (parent: StoredCase option)
        (current: StoredWork option)
        accounts
        (subject: WorkEntryMetadata)
        content
        =
        match parent with
        | Some parent when parent.CaseId = subject.CaseId ->
            let shown =
                match parent.Snapshot.State, content with
                | CaseState.Erased, EntryContent.ErasedCase -> Ok WorkDisclosure.Erased
                | (CaseState.Active _ | CaseState.Voided _), EntryContent.Work(_, snapshot) ->
                    let revision = current |> Option.map (fun (value: StoredWork) -> value.Revision)

                    if canReadVersion principal actor parent.CaseId revision subject.Revision then
                        CaseWorkReadModels.snapshot accounts snapshot
                        |> Result.map WorkDisclosure.Disclosed
                    else
                        Ok WorkDisclosure.Withheld
                | _ -> Error CoreFault.StoreIntegrityError

            shown
            |> Result.map (fun snapshot ->
                ReceiptSubject.Work
                    {
                        Reference = parent.Snapshot.Reference
                        CaseId = subject.CaseId
                        Revision = subject.Revision
                        Act = subject.Act
                        Snapshot = snapshot
                    })
        | _ -> Error CoreFault.StoreIntegrityError

    let private reminderReceipt
        principal
        actor
        (parent: StoredCase option)
        (current: Map<ReminderId, StoredReminder>)
        (subject: ReminderEntryMetadata)
        content
        =
        match parent with
        | Some parent when parent.CaseId = subject.CaseId ->
            let version id revision snapshot =
                let actual =
                    current
                    |> Map.tryFind id
                    |> Option.map (fun (value: StoredReminder) -> value.Revision)

                let disclosure =
                    match parent.Snapshot.State, snapshot with
                    | CaseState.Erased, None -> ReminderDisclosure.Erased
                    | _, Some value when
                        canReadVersion principal actor parent.CaseId actual revision
                        ->
                        ReminderDisclosure.Disclosed value
                    | _ -> ReminderDisclosure.Withheld

                {
                    ReminderVersion.ReminderId = id
                    Revision = revision
                    Snapshot = disclosure
                }

            let effects =
                match content with
                | EntryContent.ErasedCase when parent.Snapshot.State = CaseState.Erased ->
                    let primary = version subject.ReminderId subject.Revision None

                    match subject.NextReminderId with
                    | Some id ->
                        Ok(
                            ReminderReceiptEffects.CompletedAndPlanned(
                                primary,
                                version id Revision.first None
                            )
                        )
                    | None -> Ok(ReminderReceiptEffects.Single primary)
                | EntryContent.Reminder(_, effects) when
                    CaseState.lifecycle parent.Snapshot.State <> Lifecycle.Erased
                    ->
                    let shown value =
                        version (Reminder.id value) (Reminder.revision value) (Some value)

                    match effects with
                    | ReminderEffects.Created value
                    | ReminderEffects.Changed value ->
                        Ok(ReminderReceiptEffects.Single(shown value))
                    | ReminderEffects.CompletedAndPlanned(completed, next) ->
                        Ok(ReminderReceiptEffects.CompletedAndPlanned(shown completed, shown next))
                | _ -> Error CoreFault.StoreIntegrityError

            effects
            |> Result.map (fun effects ->
                ReceiptSubject.Reminder
                    {
                        Reference = parent.Snapshot.Reference
                        CaseId = subject.CaseId
                        Act = subject.Act
                        Effects = effects
                    })
        | _ -> Error CoreFault.StoreIntegrityError

    let private noteReceipt
        principal
        actor
        (parent: StoredCase option)
        current
        (subject: NoteEntryMetadata)
        content
        =
        match parent with
        | Some parent when parent.CaseId = subject.CaseId ->
            let shown =
                match content with
                | EntryContent.ErasedCase when parent.Snapshot.State = CaseState.Erased ->
                    Ok NoteDisclosure.Erased
                | EntryContent.Note({ Command = NoteCommand.RemoveNote _ }, note) when
                    parent.Snapshot.State = CaseState.Erased && Note.text note = None
                    ->
                    Ok NoteDisclosure.Erased
                | EntryContent.Note(_, note) when parent.Snapshot.State <> CaseState.Erased ->
                    if
                        Access.can
                            principal
                            actor
                            Capability.ReadHistoryFull
                            (Scope.Case parent.CaseId)
                        || (current = Some note
                            && Access.can
                                principal
                                actor
                                Capability.ReadCase
                                (Scope.Case parent.CaseId))
                    then
                        Ok(NoteDisclosure.Disclosed note)
                    else
                        Ok NoteDisclosure.Withheld
                | _ -> Error CoreFault.StoreIntegrityError

            shown
            |> Result.map (fun shown ->
                ReceiptSubject.Note
                    {
                        Reference = parent.Snapshot.Reference
                        CaseId = subject.CaseId
                        NoteId = subject.NoteId
                        Act = subject.Act
                        Snapshot = shown
                    })
        | _ -> Error CoreFault.StoreIntegrityError

    let internal receipt
        (basis: WorkDateBasis)
        principal
        actor
        (recordedBy: ActorRef option)
        current
        (validated: ValidatedEntry)
        =
        let entry = validated.Entry

        let subject =
            match entry.Subject, validated.Content, current with
            | EntryTarget.Case _, _, ReceiptCurrent.Case record ->
                caseReceipt principal actor record validated
            | EntryTarget.Work subject, content, ReceiptCurrent.Work(parent, work, accounts) ->
                workReceipt principal actor parent work accounts subject content
            | EntryTarget.Note subject, content, ReceiptCurrent.Note(parent, note) ->
                noteReceipt principal actor parent note subject content
            | EntryTarget.Reminder subject, content, ReceiptCurrent.Reminder(parent, reminders) ->
                reminderReceipt principal actor parent reminders subject content
            | EntryTarget.People(target, act),
              (EntryContent.People _ | EntryContent.ErasedProfile),
              ReceiptCurrent.People actorRef ->
                let shown =
                    if Access.can principal actor Capability.ReadAccess Scope.Installation then
                        match actorRef with
                        | Some reference when reference.ActorId = target ->
                            Ok(PeopleTargetDisclosure.Disclosed reference)
                        | _ -> Error CoreFault.StoreIntegrityError
                    else
                        Ok(PeopleTargetDisclosure.Withheld target)

                shown
                |> Result.map (fun shown ->
                    ReceiptSubject.People
                        {
                            TargetActor = shown
                            Act = act
                            Effects =
                                [
                                    {
                                        PeopleReceiptEffect.Act = act
                                        Id = entry.Id
                                        Position = entry.Position
                                    }
                                ]
                        })
            | _ -> Error CoreFault.StoreIntegrityError

        subject
        |> Result.bind (fun subject ->
            match recordedBy with
            | Some author when author.ActorId = entry.ActorId ->
                Ok
                    {
                        OperationId = entry.OperationId
                        Position = position entry.Sequence entry.EntryHash
                        Subject = subject
                        RecordedAt = entry.RecordedAt
                        RecordedBy = author
                        Basis = basis
                    }
            | _ -> Error CoreFault.StoreIntegrityError)

    let internal receiptOperation
        basis
        principal
        actor
        recordedBy
        current
        (operation: ValidatedOperation)
        =
        receipt basis principal actor recordedBy current (List.head operation.ValidatedEntries)
        |> Result.map (fun receipt ->
            let subject =
                match
                    receipt.Subject,
                    (match operation.Stored.Root.Structure.Command with
                     | CommandSkeleton.People command -> Some(PeopleAct.kind command.Act)
                     | _ -> None)
                with
                | ReceiptSubject.People people, Some kind ->
                    ReceiptSubject.People
                        { people with
                            Act = kind
                            Effects =
                                operation.ValidatedEntries
                                |> List.map (fun validated ->
                                    match validated.Entry.Subject with
                                    | EntryTarget.People(_, act) ->
                                        {
                                            PeopleReceiptEffect.Act = act
                                            Id = validated.Entry.Id
                                            Position = validated.Entry.Position
                                        }
                                    | _ ->
                                        invalidOp
                                            "A validated people group contains only people effects.")
                        }
                | subject, _ -> subject

            { receipt with
                Position = operation.Stored.Root.Position
                Subject = subject
            })

module internal OperationIdentity =
    let internal decide
        (services: ServiceContext)
        actor
        (request: OperationIdentityRequest)
        receipt
        =
        let operationId = request.OperationId
        let bytes = request.Bytes
        let existing = request.Existing
        let installationId = services.InstallationId
        let principal = services.Principal

        match existing with
        | None -> Verdict.Proceed()
        | Some entry when
            not (LedgerEntries.canObserve principal actor (LedgerEntries.primary entry).Subject)
            ->
            Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
        | Some entry when entry.Root.Header.OperationId <> operationId ->
            Verdict.Fail CoreFault.StoreIntegrityError
        | Some entry ->
            match LedgerEntries.validateOperation installationId entry with
            | Error fault -> Verdict.Fail fault
            | Ok validated ->
                match LedgerEntries.identityBytesOperation validated with
                | None -> Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
                | Some accepted when accepted = bytes ->
                    receipt validated
                    |> Result.map Verdict.Replay
                    |> Result.defaultWith Verdict.Fail
                | Some _ -> Verdict.Refuse Refusal.IdempotencyConflict
