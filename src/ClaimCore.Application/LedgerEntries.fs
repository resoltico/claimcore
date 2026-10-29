namespace ClaimCore.Application


open System
open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.RecordFormat

[<RequireQualifiedAccess; NoComparison>]
type internal EntryContent =
    | Case of request: CaseCommand * snapshot: CaseSnapshot option
    | Authority of AuthorityRequestRecord
    | ErasedCase

[<NoComparison>]
type internal ValidatedEntry =
    private
        {
            InstallationId: Guid
            Entry: StoredEntry
            Content: EntryContent
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

type internal OperationIdentityRequest =
    {
        OperationId: OperationId
        Bytes: byte array
        Existing: StoredEntry option
    }

module LedgerEntries =
    let private width expected (bytes: byte array) =
        not (obj.ReferenceEquals(bytes, null)) && bytes.Length = expected

    let position sequence (hash: byte array) : LedgerPosition =
        EntryHash.ofBytes hash
        |> Option.map (LedgerPosition.create sequence)
        |> Option.defaultWith (fun () -> invalidArg (nameof hash) "Invalid ledger position.")

    let internal stored (entry: EntryDraft) : StoredEntry =
        {
            Sequence = entry.Sequence
            EntryHash = entry.EntryHash
            OperationId = entry.OperationId
            ActorId = entry.ActorId
            RecordedAt = entry.RecordedAt
            Subject = entry.Subject
            PayloadCommitment = entry.PayloadCommitment
            PayloadNonce = Some entry.PayloadNonce
            Payload = Some entry.Payload
        }

    let private metadataSubject =
        function
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

    let internal create (input: EntryCreation) : EntryDraft =
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

        let metadata =
            {
                InstallationId = installationId
                Sequence = sequence
                PreviousHash = EntryHash.toBytes head.EntryHash
                OperationId = operationId
                ActorId = actorId
                RecordedAt = instant
                Subject = metadataSubject subject
                PayloadCommitment = commitment
            }

        {
            Sequence = sequence
            PreviousHash = EntryHash.toBytes head.EntryHash
            EntryHash = LedgerEntry.hash metadata
            OperationId = operationId
            ActorId = actorId
            RecordedAt = instant
            Subject = subject
            PayloadCommitment = commitment
            PayloadNonce = Array.copy nonce
            Payload = payload
        }

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
        | EntryTarget.Authority _ ->
            Authority.can principal actor Capability.ReadAuthority Scope.Installation

    let private validMetadata (entry: StoredEntry) =
        let subject =
            match entry.Subject with
            | EntryTarget.Case(_, revision, act, _) ->
                revision >= Revision.first
                && ((revision = Revision.first) = (act = CaseActKind.Open))
            | EntryTarget.Authority _ -> true

        entry.Sequence >= Sequence.first
        && width 32 entry.EntryHash
        && width 32 entry.PayloadCommitment
        && entry.RecordedAt.UtcTicks % 10L = 0L
        && subject

    let private caseContent installationId (entry: StoredEntry) (data: LedgerPayload) =
        match entry.Subject with
        | EntryTarget.Authority _ -> Error CoreFault.StoreIntegrityError
        | EntryTarget.Case(id, revision, act, _) ->
            match RequestRecord.decode RecordLimits.MaximumRequestRecordBytes data.Request with
            | Ok request when
                request.OperationId = entry.OperationId
                && Revision.value request.ExpectedRevision = Revision.value revision - 1L
                && CaseAct.kind request.Act = act
                && CaseReferenceId.encode installationId (CaseReference.format request.Reference) =
                    id
                ->
                if act = CaseActKind.Erase then
                    if Array.isEmpty data.Snapshot then
                        Ok(EntryContent.Case(request, None))
                    else
                        Error CoreFault.StoreIntegrityError
                else
                    match ClaimCore.RecordFormat.CaseRecord.decodeSnapshot data.Snapshot with
                    | Ok snapshot when
                        snapshot.Revision = revision
                        && CaseReference.identity snapshot.Reference =
                            CaseReference.identity request.Reference
                        ->
                        Ok(EntryContent.Case(request, Some snapshot))
                    | _ -> Error CoreFault.StoreIntegrityError
            | _ -> Error CoreFault.StoreIntegrityError

    let private authorityContent (entry: StoredEntry) target act (data: LedgerPayload) =
        match AuthorityRecord.decode RecordLimits.MaximumRequestRecordBytes data.Request with
        | Ok request when
            request.OperationId = OperationId.value entry.OperationId
            && AuthorityAct.kind request.Act = act
            && AuthorityRequests.validRecord request
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

    let internal validate installationId (entry: StoredEntry) =
        if installationId = Guid.Empty || not (validMetadata entry) then
            Error CoreFault.StoreIntegrityError
        else
            let content =
                match entry.Subject, entry.Payload, entry.PayloadNonce with
                | EntryTarget.Case _, None, None -> Ok EntryContent.ErasedCase
                | _ ->
                    payload entry
                    |> Result.bind (fun data ->
                        match entry.Subject with
                        | EntryTarget.Case _ -> caseContent installationId entry data
                        | EntryTarget.Authority(target, act) ->
                            authorityContent entry target act data)

            content
            |> Result.map (fun content ->
                {
                    InstallationId = installationId
                    Entry = entry
                    Content = content
                })

    /// Only a validated authority payload can supply typed audit facts.
    let internal authorityHistory (validated: ValidatedEntry) =
        let entry = validated.Entry

        match entry.Subject, validated.Content with
        | EntryTarget.Authority(target, act), EntryContent.Authority request ->
            AuthorityRequests.fromRecord request
            |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
            |> Result.bind (fun command ->
                let facts = AuthorityHistoryFacts.ofCommand command

                if AuthorityHistoryFacts.kind facts <> act then
                    Error CoreFault.StoreIntegrityError
                else
                    Ok
                        {
                            AuthorityHistoryEntry.Sequence = entry.Sequence
                            RecordedAt = entry.RecordedAt
                            RecordedBy = entry.ActorId
                            TargetActorId = target
                            Facts = facts
                        })
        | _ -> Error CoreFault.StoreIntegrityError

    /// Comparison bytes are canonical; retained ledger payloads remain authored.
    let internal identityBytes validated =
        match validated.Content with
        | EntryContent.Case(request, _) -> Some(Case.canonical request |> RequestRecord.encode)
        | EntryContent.Authority request ->
            Some(AuthorityRequests.canonical request |> AuthorityRecord.encode)
        | EntryContent.ErasedCase -> None

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
        let installationId = validated.InstallationId
        let content = validated.Content

        match validated.Entry.Subject with
        | EntryTarget.Authority _ -> Error CoreFault.StoreIntegrityError
        | EntryTarget.Case(id, revision, act, _) ->
            match current with
            | None -> Error CoreFault.StoreIntegrityError
            | Some record when
                record.CaseId <> id
                || record.Snapshot.Revision < revision
                || (Case.ofAccepted record.Snapshot |> Result.isError)
                ->
                Error CoreFault.StoreIntegrityError
            | Some record when
                CaseReferenceId.encode
                    installationId
                    (CaseReference.format record.Snapshot.Reference)
                <> id
                ->
                Error CoreFault.StoreIntegrityError
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
                            Reference = record.Snapshot.Reference
                            Revision = revision
                            Act = act
                            Snapshot = snapshot
                        })

    let internal receipt principal actor current (validated: ValidatedEntry) =
        let entry = validated.Entry

        let subject =
            match entry.Subject, validated.Content with
            | EntryTarget.Case _, _ -> caseReceipt principal actor current validated
            | EntryTarget.Authority(target, act), EntryContent.Authority _ ->
                Ok(ReceiptSubject.Authority { TargetActorId = target; Act = act })
            | _ -> Error CoreFault.StoreIntegrityError

        subject
        |> Result.map (fun subject ->
            {
                OperationId = entry.OperationId
                Position = position entry.Sequence entry.EntryHash
                Subject = subject
                RecordedAt = entry.RecordedAt
                RecordedBy = entry.ActorId
            })

    let internal summary (current: StoredCase) (entry: StoredEntry) =
        match entry.Subject with
        | EntryTarget.Case(id, revision, act, _) when
            validMetadata entry
            && id = current.CaseId
            && revision <= current.Snapshot.Revision
            ->
            Ok
                {
                    OperationId = entry.OperationId
                    Position = position entry.Sequence entry.EntryHash
                    Revision = revision
                    Act = act
                    RecordedAt = entry.RecordedAt
                    RecordedBy = entry.ActorId
                }
        | _ -> Error CoreFault.StoreIntegrityError


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
            match LedgerEntries.validate installationId entry with
            | Error fault -> Verdict.Fail fault
            | Ok validated ->
                match LedgerEntries.identityBytes validated with
                | None -> Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable)
                | Some accepted when accepted = bytes ->
                    receipt validated
                    |> Result.map Verdict.Replay
                    |> Result.defaultWith Verdict.Fail
                | Some _ -> Verdict.Refuse Refusal.IdempotencyConflict
