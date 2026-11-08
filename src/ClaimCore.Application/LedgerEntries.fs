namespace ClaimCore.Application


open System
open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

[<RequireQualifiedAccess; NoComparison>]
type internal EntryContent =
    | Case of request: CaseCommand * snapshot: CaseSnapshot option
    | Authority of AuthorityEffectRecord
    | Work of WorkRequestRecord * CaseWorkSnapshot
    | Reminder of ReminderRequestRecord * ReminderEffects
    | ErasedCase
    | ErasedProfile

[<NoComparison>]
type internal ValidatedEntry =
    private
        {
            InstallationId: Guid
            Entry: StoredEntry
            Content: EntryContent
        }

[<NoComparison>]
type internal ValidatedOperation =
    private
        {
            Stored: StoredOperation
            ValidatedEntries: ValidatedEntry list
        }

type internal EntryCreation =
    {
        InstallationId: Guid
        Head: LedgerPosition
        Instant: DateTimeOffset
        Nonce: byte array
        ActorId: ActorId
        OperationId: OperationId
        Subject: EntryTarget
        Request: byte array
        Snapshot: byte array
    }

type internal LedgerEffectCreation =
    {
        Subject: EntryTarget
        Request: byte array
        Snapshot: byte array
    }

type internal AuthorityOperationCreation =
    {
        InstallationId: Guid
        Head: LedgerPosition
        Instant: DateTimeOffset
        ActorId: ActorId
        OperationId: OperationId
        Effects: LedgerEffectCreation list
    }

[<NoComparison>]
type internal OperationIdentityRequest =
    {
        OperationId: OperationId
        Bytes: byte array
        Existing: StoredOperation option
    }

module LedgerEntries =
    let internal content (validated: ValidatedEntry) = validated.Content

    let private width expected (bytes: byte array) =
        not (obj.ReferenceEquals(bytes, null)) && bytes.Length = expected

    let position sequence (hash: byte array) : LedgerPosition =
        EntryHash.ofBytes hash
        |> Option.map (LedgerPosition.create sequence)
        |> Option.defaultWith (fun () -> invalidArg (nameof hash) "Invalid ledger position.")

    let internal stored (entry: EntryDraft) : StoredEntry =
        {
            Effects = entry.Effects
            Sequence = entry.Sequence
            EntryHash = entry.EntryHash
            OperationId = entry.OperationId
            ActorId = entry.ActorId
            RecordedAt = entry.RecordedAt
            Subject = entry.Subject
            CreationReference = entry.CreationReference
            PayloadCommitment = entry.PayloadCommitment
            PayloadNonce = Some entry.PayloadNonce
            Payload = Some entry.Payload
        }

    let private metadataSubject =
        function
        | EntryTarget.Work subject -> RecordedTarget.Work subject
        | EntryTarget.Reminder subject -> RecordedTarget.Reminder subject
        | EntryTarget.Authority(target, act) ->
            RecordedTarget.Authority { TargetActorId = target; Act = act }
        | EntryTarget.Case(id, revision, act, date) ->
            RecordedTarget.Case
                {
                    CaseId = id
                    Revision = revision
                    Act = act
                    BusinessDate = date
                }

    let internal createEffect effects (input: EntryCreation) : EntryDraft =
        let installationId = input.InstallationId
        let head = input.Head
        let instant = input.Instant
        let nonce = input.Nonce
        let actorId = input.ActorId
        let operationId = input.OperationId
        let subject = input.Subject
        let request = input.Request
        let snapshot = input.Snapshot

        let payload =
            LedgerEntry.encodePayload
                {
                    Request = request
                    Snapshot = snapshot
                }

        if payload.Length > LedgerPayload.MaximumBytes then
            invalidArg (nameof request) "Ledger payload exceeds its format bound."

        let commitment = LedgerEntry.commitment nonce payload
        let sequence = Sequence.successor head.Sequence

        let creationReference =
            match subject with
            | EntryTarget.Case(_, _, CaseActKind.Open, _) ->
                match RequestRecord.decode RecordLimits.MaximumRequestRecordBytes request with
                | Ok command -> Some command.Reference
                | Error _ -> invalidArg (nameof request) "Invalid OPEN creation identity."
            | _ -> None

        let metadata =
            {
                Effects = effects
                InstallationId = installationId
                Sequence = sequence
                PreviousHash = EntryHash.toBytes head.EntryHash
                OperationId = operationId
                ActorId = actorId
                RecordedAt = instant
                Subject = metadataSubject subject
                CreationReference = creationReference
                PayloadCommitment = commitment
            }

        {
            Effects = effects
            Sequence = sequence
            PreviousHash = EntryHash.toBytes head.EntryHash
            EntryHash = LedgerEntry.hash metadata
            OperationId = operationId
            ActorId = actorId
            RecordedAt = instant
            Subject = subject
            CreationReference = creationReference
            PayloadCommitment = commitment
            PayloadNonce = Array.copy nonce
            Payload = payload
        }

    let internal create (input: EntryCreation) =
        let effects =
            match input.Subject with
            | EntryTarget.Authority(_, act) -> LedgerEffects.authority act 1 0
            | _ -> LedgerEffects.single

        createEffect effects input

    /// Every physical effect gets a fresh salt and extends the previous effect's hash.
    let internal createAuthorityOperation
        operation
        newPayloadNonce
        (input: AuthorityOperationCreation)
        =
        let count = input.Effects.Length

        if
            count < 1
            || count > LedgerEffects.MaximumCount
            || Sequence.value input.Head.Sequence > Sequence.Maximum - int64 count
        then
            Error CoreFault.InternalError
        else
            try
                let _, reversed =
                    input.Effects
                    |> List.indexed
                    |> List.fold
                        (fun (head, entries) (index, effect) ->
                            let entry =
                                createEffect
                                    (LedgerEffects.authority operation count index)
                                    {
                                        InstallationId = input.InstallationId
                                        Head = head
                                        Instant = input.Instant
                                        Nonce = newPayloadNonce ()
                                        ActorId = input.ActorId
                                        OperationId = input.OperationId
                                        Subject = effect.Subject
                                        Request = effect.Request
                                        Snapshot = effect.Snapshot
                                    }

                            position entry.Sequence entry.EntryHash, entry :: entries)
                        (input.Head, [])

                let entries = List.rev reversed

                StoredOperation.create (entries |> List.map stored)
                |> Result.map (fun _ -> entries)
            with
            | :? ArgumentException
            | :? OverflowException -> Error CoreFault.InternalError

    /// The existing salted commitment protects ordinary reads and replay before content is interpreted.
    let internal payload (entry: StoredEntry) =
        match entry.Payload, entry.PayloadNonce with
        | Some bytes, Some nonce when width 32 nonce && width 32 entry.PayloadCommitment ->
            match LedgerEntry.decodePayload LedgerPayload.MaximumBytes bytes with
            | Error _ -> Error CoreFault.StoreIntegrityError
            | Ok value ->
                if
                    CryptographicOperations.FixedTimeEquals(
                        LedgerEntry.commitment nonce bytes,
                        entry.PayloadCommitment
                    )
                then
                    Ok value
                else
                    Error CoreFault.OperationContentConflict
        | _ -> Error CoreFault.StoreIntegrityError

    let internal canObserve principal actor =
        function
        | EntryTarget.Case(id, _, _, _) ->
            Authority.can principal actor Capability.ReadHistory (Scope.Case id)
        | EntryTarget.Work subject ->
            Authority.can principal actor Capability.ReadHistory (Scope.Case subject.CaseId)
        | EntryTarget.Reminder subject ->
            Authority.can principal actor Capability.ReadHistory (Scope.Case subject.CaseId)
        | EntryTarget.Authority _ ->
            Authority.can principal actor Capability.ReadAuthority Scope.Installation

    let private validMetadata (entry: StoredEntry) =
        let subject =
            match entry.Subject with
            | EntryTarget.Case(_, revision, act, _) ->
                revision >= Revision.first
                && ((revision = Revision.first) = (act = CaseActKind.Open))
                && (entry.CreationReference.IsSome = (act = CaseActKind.Open))
            | EntryTarget.Authority _ -> entry.CreationReference.IsNone
            | EntryTarget.Work subject ->
                subject.Revision >= Revision.first && entry.CreationReference.IsNone
            | EntryTarget.Reminder subject ->
                subject.Revision >= Revision.first
                && entry.CreationReference.IsNone
                && ((subject.Act = ReminderActKind.Create) = (subject.Revision = Revision.first))
                && (if subject.Act = ReminderActKind.Create then
                        subject.ReminderId = ReminderId.ofOperationId entry.OperationId
                    else
                        true)
                && (match subject.Act, subject.NextReminderId with
                    | ReminderActKind.CompleteAndPlanNext, Some id ->
                        id = ReminderId.ofOperationId entry.OperationId && id <> subject.ReminderId
                    | ReminderActKind.CompleteAndPlanNext, None -> false
                    | _, None -> true
                    | _, Some _ -> false)

        entry.Sequence >= Sequence.first
        && width 32 entry.EntryHash
        && width 32 entry.PayloadCommitment
        && entry.RecordedAt.UtcTicks % 10L = 0L
        && (match entry.Subject with
            | EntryTarget.Authority(_, act) -> LedgerEffects.describesAuthority act entry.Effects
            | _ -> entry.Effects = LedgerEffects.single)
        && subject

    let private caseRequestMatches (entry: StoredEntry) id revision act (request: CaseCommand) =
        request.OperationId = entry.OperationId
        && Revision.value request.ExpectedRevision = Revision.value revision - 1L
        && CaseAct.kind request.Act = act
        && (act <> CaseActKind.Open
            || (id = CaseId.ofOperationId request.OperationId
                && entry.CreationReference = Some request.Reference))

    let private caseContent _installationId (entry: StoredEntry) (data: LedgerPayload) =
        match entry.Subject with
        | EntryTarget.Case(id, revision, act, _) ->
            match RequestRecord.decode RecordLimits.MaximumRequestRecordBytes data.Request with
            | Ok request when caseRequestMatches entry id revision act request ->
                if act = CaseActKind.Erase then
                    if Array.isEmpty data.Snapshot then
                        Ok(EntryContent.Case(request, None))
                    else
                        Error CoreFault.StoreIntegrityError
                else
                    match ClaimCore.RecordFormat.CaseRecord.decodeSnapshot data.Snapshot with
                    | Ok snapshot when
                        snapshot.CaseId = id
                        && snapshot.Revision = revision
                        && CaseReference.identity snapshot.Reference =
                            CaseReference.identity request.Reference
                        ->
                        Ok(EntryContent.Case(request, Some snapshot))
                    | _ -> Error CoreFault.StoreIntegrityError
            | _ -> Error CoreFault.StoreIntegrityError
        | _ -> Error CoreFault.StoreIntegrityError

    let private authorityContent (entry: StoredEntry) target act (data: LedgerPayload) =
        match AuthorityRecord.decode RecordLimits.MaximumRequestRecordBytes data.Request with
        | Ok request when
            request.OperationId = OperationId.value entry.OperationId
            && AuthorityEffect.kind request.Effect = act
            && request.TargetActorId = target
            && Array.isEmpty data.Snapshot
            ->
            let identity =
                match act with
                | AuthorityKind.ProvisionOwner ->
                    target = ActorId.ofOperationId entry.OperationId
                    && entry.ActorId = target
                    && entry.Sequence = Sequence.first
                | AuthorityKind.Register ->
                    target = ActorId.ofOperationId entry.OperationId && entry.ActorId <> target
                | _ -> true

            if identity then
                Ok(EntryContent.Authority request)
            else
                Error CoreFault.StoreIntegrityError
        | _ -> Error CoreFault.StoreIntegrityError

    let private workContent
        (entry: StoredEntry)
        (subject: WorkEntryMetadata)
        (data: LedgerPayload)
        =
        match WorkRecord.decodeRequest data.Request, WorkRecord.decodeSnapshot data.Snapshot with
        | Ok request, Ok snapshot when
            request.OperationId = entry.OperationId
            && request.CaseId = subject.CaseId
            && Revision.value request.ExpectedRevision < Revision.Maximum
            && snapshot.CaseId = subject.CaseId
            && snapshot.Revision = subject.Revision
            && Revision.successor request.ExpectedRevision = subject.Revision
            ->
            Ok(EntryContent.Work(request, snapshot))
        | _ -> Error CoreFault.StoreIntegrityError

    let private reminderContent
        (entry: StoredEntry)
        (subject: ReminderEntryMetadata)
        (data: LedgerPayload)
        =
        match
            ReminderRecord.decodeRequest data.Request, ReminderRecord.decodeEffects data.Snapshot
        with
        | Ok request, Ok effects when
            request.OperationId = entry.OperationId
            && ReminderCommand.parent request.Command = subject.CaseId
            && ReminderCommand.target request.OperationId request.Command = subject.ReminderId
            && ReminderCommand.resultingRevision request.Command = Some subject.Revision
            && ReminderCommand.kind request.Command = subject.Act
            && HistoricalWorkEffects.boundedReminderRequest request effects
            ->
            Ok(EntryContent.Reminder(request, effects))
        | _ -> Error CoreFault.StoreIntegrityError

    let internal validate installationId (entry: StoredEntry) =
        if installationId = Guid.Empty || not (validMetadata entry) then
            Error CoreFault.StoreIntegrityError
        else
            let content =
                match entry.Subject, entry.Payload, entry.PayloadNonce with
                | (EntryTarget.Case _ | EntryTarget.Work _ | EntryTarget.Reminder _), None, None ->
                    Ok EntryContent.ErasedCase
                | EntryTarget.Authority(_, AuthorityKind.SetProfile), None, None ->
                    Ok EntryContent.ErasedProfile
                | _ ->
                    payload entry
                    |> Result.bind (fun data ->
                        match entry.Subject with
                        | EntryTarget.Case _ -> caseContent installationId entry data
                        | EntryTarget.Work subject -> workContent entry subject data
                        | EntryTarget.Reminder subject -> reminderContent entry subject data
                        | EntryTarget.Authority(target, act) ->
                            authorityContent entry target act data)

            content
            |> Result.map (fun content ->
                {
                    InstallationId = installationId
                    Entry = entry
                    Content = content
                })

    let internal validateOperation installation (operation: StoredOperation) =
        operation.Entries
        |> List.fold
            (fun state entry ->
                state
                |> Result.bind (fun entries ->
                    validate installation entry |> Result.map (fun value -> value :: entries)))
            (Ok [])
        |> Result.map (fun reversed ->
            {
                Stored = operation
                ValidatedEntries = List.rev reversed
            })

    let private authorityEffectFacts effect =
        let principal (input: PrincipalInput) =
            PrincipalKey.ofKind input.Kind input.Issuer input.Subject
            |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)

        match effect with
        | AuthorityEffect.Register input ->
            principal input |> Result.map AuthorityHistoryFacts.Registered
        | AuthorityEffect.ProvisionOwner input ->
            principal input |> Result.map AuthorityHistoryFacts.OwnerProvisioned
        | AuthorityEffect.SetProfile profile -> Ok(AuthorityHistoryFacts.ProfileSet(Some profile))
        | AuthorityEffect.EraseProfile -> Ok AuthorityHistoryFacts.ProfileErased
        | AuthorityEffect.Enable -> Ok AuthorityHistoryFacts.Enabled
        | AuthorityEffect.Disable -> Ok AuthorityHistoryFacts.Disabled
        | AuthorityEffect.Grant grant ->
            Ok(AuthorityHistoryFacts.Granted(AuthorityRequests.display grant))
        | AuthorityEffect.Revoke grant ->
            Ok(AuthorityHistoryFacts.Revoked(AuthorityRequests.display grant))

    let private authorityHistoryFacts act content =
        match content with
        | EntryContent.ErasedProfile when act = AuthorityKind.SetProfile ->
            Ok(AuthorityHistoryFacts.ProfileSet None)
        | EntryContent.Authority record -> authorityEffectFacts record.Effect
        | _ -> Error CoreFault.StoreIntegrityError

    /// Only a validated authority payload can supply typed audit facts.
    let internal authorityHistory (actors: Map<ActorId, ActorRef>) (validated: ValidatedEntry) =
        let entry = validated.Entry

        match entry.Subject, validated.Content with
        | EntryTarget.Authority(target, act), content ->
            authorityHistoryFacts act content
            |> Result.bind (fun facts ->
                if AuthorityHistoryFacts.kind facts <> act then
                    Error CoreFault.StoreIntegrityError
                else
                    match Map.tryFind entry.ActorId actors, Map.tryFind target actors with
                    | Some author, Some targetRef when
                        author.ActorId = entry.ActorId && targetRef.ActorId = target
                        ->
                        Ok
                            {
                                AuthorityHistoryEntry.Sequence = entry.Sequence
                                RecordedAt = entry.RecordedAt
                                RecordedBy = author
                                Target = targetRef
                                Facts = facts
                            }
                    | _ -> Error CoreFault.StoreIntegrityError)
        | _ -> Error CoreFault.StoreIntegrityError

    /// Comparison bytes are canonical; retained ledger payloads remain authored.
    let internal identityBytes validated =
        match validated.Content with
        | EntryContent.Case(request, _) ->
            match validated.Entry.Subject with
            | EntryTarget.Case(id, _, _, _) -> Some(RequestRecord.identity id request)
            | _ -> None
        | EntryContent.Work(request, _) -> Some(WorkRecord.encodeRequest request)
        | EntryContent.Reminder(request, _) -> Some(ReminderRecord.encodeRequest request)
        | EntryContent.Authority request ->
            Some(AuthorityRequests.canonical request |> AuthorityRecord.encode)
        | EntryContent.ErasedCase
        | EntryContent.ErasedProfile -> None

    /// Canonical request bytes exist only while every required effect payload remains available.
    let internal authorityIdentityBytes operation (requests: byte array list) =
        match requests with
        | [ request ] -> request
        | requests ->
            if requests.IsEmpty || requests.Length > LedgerEffects.MaximumCount then
                invalidArg (nameof requests) "Use one bounded authority operation."

            let frame (bytes: byte array) =
                let size = Array.zeroCreate<byte> 4
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(Span size, bytes.Length)
                Array.append size bytes

            Array.concat
                [
                    yield
                        System.Text.Encoding.ASCII.GetBytes
                            "claimcore/authority-operation-identity/1\n"
                    yield
                        AuthorityKind.token operation
                        |> System.Text.Encoding.ASCII.GetBytes
                        |> frame
                    yield! requests |> List.map frame
                ]

    let internal identityBytesOperation (operation: ValidatedOperation) =
        let requests =
            operation.ValidatedEntries
            |> List.fold
                (fun state entry ->
                    Option.map2
                        (fun requests value -> value :: requests)
                        state
                        (identityBytes entry))
                (Some [])
            |> Option.map List.rev

        requests
        |> Option.map (fun requests ->
            match LedgerEffects.authorityOperation operation.Stored.Root.Effects, requests with
            | Some kind, requests -> authorityIdentityBytes kind requests
            | None, [ request ] -> request
            | _ -> invalidOp "A non-authority operation has one effect.")

    /// The same snapshot-disclosure policy shapes append, replay, preview, observation and history.
    let private disclosure principal actor (current: StoredCase) revision snapshot =
        let id = current.CaseId
        let currentRevision = current.Snapshot.Revision

        if
            Authority.can principal actor Capability.ReadHistoryFull (Scope.Case id)
            || (currentRevision = revision
                && Authority.can principal actor Capability.ReadCase (Scope.Case id))
        then
            SnapshotDisclosure.Disclosed snapshot
        else
            SnapshotDisclosure.Withheld

    let private caseReceipt
        principal
        actor
        (current: StoredCase option)
        (validated: ValidatedEntry)
        =
        let content = validated.Content

        match validated.Entry.Subject with
        | EntryTarget.Authority _
        | EntryTarget.Work _
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
                    | (CaseState.Active _ | CaseState.Voided _), EntryContent.Case(_, Some snapshot) when
                        snapshot.Reference = record.Snapshot.Reference
                        ->
                        match LiveCase.ofState snapshot.State with
                        | None -> Error CoreFault.StoreIntegrityError
                        | Some live -> Ok(disclosure principal actor record revision live)
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
        Authority.can principal actor Capability.ReadHistoryFull (Scope.Case parentId)
        || (currentRevision = Some revision
            && Authority.can principal actor Capability.ReadCase (Scope.Case parentId))

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
            | EntryTarget.Reminder subject, content, ReceiptCurrent.Reminder(parent, reminders) ->
                reminderReceipt principal actor parent reminders subject content
            | EntryTarget.Authority(target, act),
              (EntryContent.Authority _ | EntryContent.ErasedProfile),
              ReceiptCurrent.Authority actorRef ->
                let shown =
                    if
                        Authority.can principal actor Capability.ReadAuthority Scope.Installation
                    then
                        match actorRef with
                        | Some reference when reference.ActorId = target ->
                            Ok(AuthorityTargetDisclosure.Disclosed reference)
                        | _ -> Error CoreFault.StoreIntegrityError
                    else
                        Ok(AuthorityTargetDisclosure.Withheld target)

                shown
                |> Result.map (fun shown ->
                    ReceiptSubject.Authority
                        {
                            TargetActor = shown
                            Act = act
                            Effects =
                                [
                                    {
                                        AuthorityReceiptEffect.Act = act
                                        Position = position entry.Sequence entry.EntryHash
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
                    receipt.Subject, LedgerEffects.authorityOperation operation.Stored.Root.Effects
                with
                | ReceiptSubject.Authority authority, Some kind ->
                    ReceiptSubject.Authority
                        { authority with
                            Act = kind
                            Effects =
                                operation.ValidatedEntries
                                |> List.map (fun validated ->
                                    match validated.Entry.Subject with
                                    | EntryTarget.Authority(_, act) ->
                                        {
                                            AuthorityReceiptEffect.Act = act
                                            Position =
                                                position
                                                    validated.Entry.Sequence
                                                    validated.Entry.EntryHash
                                        }
                                    | _ ->
                                        invalidOp
                                            "A validated authority group contains only authority effects.")
                        }
                | subject, _ -> subject

            { receipt with
                Position = operation.Stored.Position
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
        | Some entry when not (LedgerEntries.canObserve principal actor entry.Subject) ->
            Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
        | Some entry when entry.OperationId <> operationId ->
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
