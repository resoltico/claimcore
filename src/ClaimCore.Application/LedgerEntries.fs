namespace ClaimCore.Application

open ClaimCore.Contract

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

module internal LedgerEntries =
    let private width expected (bytes: byte array) =
        not (obj.ReferenceEquals(bytes, null)) && bytes.Length = expected

    let position sequence (hash: byte array) : LedgerPosition =
        {
            Sequence = sequence
            EntryHash = Convert.ToHexStringLower hash
        }

    let stored (entry: EntryDraft) : StoredEntry =
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
        | StoredSubject.Authority(target, act) ->
            LedgerSubject.Authority { TargetActorId = target; Act = act }
        | StoredSubject.Case(id, revision, act, date) ->
            LedgerSubject.Case
                {
                    CaseId = id
                    Revision = revision
                    Act = act
                    BusinessDate = date
                }

    let create
        installationId
        (head: LedgerHead)
        instant
        nonce
        actorId
        operationId
        subject
        request
        snapshot
        : EntryDraft =
        if head.Sequence < 0L || head.Sequence >= Revision.Maximum then
            invalidArg (nameof head) "Ledger head cannot advance."

        let payload =
            LedgerEntry.encodePayload
                {
                    Request = request
                    Snapshot = snapshot
                }

        if payload.Length > Formats.MaximumPayloadBytes then
            invalidArg (nameof request) "Ledger payload exceeds its format bound."

        let commitment = LedgerEntry.commitment nonce payload
        let sequence = head.Sequence + 1L

        let metadata =
            {
                InstallationId = installationId
                Sequence = sequence
                PreviousHash = head.EntryHash
                OperationId = operationId
                ActorId = actorId
                RecordedAt = instant
                Subject = metadataSubject subject
                PayloadCommitment = commitment
            }

        {
            Sequence = sequence
            PreviousHash = Array.copy head.EntryHash
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
    let payload (entry: StoredEntry) =
        match entry.Payload, entry.PayloadNonce with
        | Some bytes, Some nonce when width 32 nonce && width 32 entry.PayloadCommitment ->
            match LedgerEntry.decodePayload Formats.MaximumPayloadBytes bytes with
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

    let canObserve principal actor =
        function
        | StoredSubject.Case(id, _, _, _) ->
            ActorAuthorization.can principal actor Capability.ReadHistory (ResourceScope.Case id)
        | StoredSubject.Authority _ ->
            ActorAuthorization.can
                principal
                actor
                Capability.ReadAuthority
                ResourceScope.Installation

    let private validMetadata (entry: StoredEntry) =
        let subject =
            match entry.Subject with
            | StoredSubject.Case(id, revision, act, _) ->
                id <> Guid.Empty
                && revision >= 1L
                && revision <= Revision.Maximum
                && ((revision = 1L) = (act = (CaseActKind.Business BusinessActKind.Open)))
            | StoredSubject.Authority(id, _) -> id <> Guid.Empty

        entry.Sequence >= 1L
        && entry.Sequence <= Revision.Maximum
        && width 32 entry.EntryHash
        && width 32 entry.PayloadCommitment
        && entry.OperationId <> Guid.Empty
        && entry.ActorId <> Guid.Empty
        && entry.RecordedAt.UtcTicks % 10L = 0L
        && subject

    let private caseContent
        installationId
        (entry: StoredEntry)
        id
        revision
        act
        (data: LedgerPayload)
        =
        match RequestRecord.decode Limits.MaximumRequestBytes data.Request with
        | Ok request when
            request.OperationId = entry.OperationId
            && request.ExpectedRevision = revision - 1L
            && CaseActs.kind request.Act = act
            && CaseReferenceId.encode installationId (CaseReference.format request.Reference) = id
            ->
            if act = (CaseActKind.Stewardship StewardshipActKind.Erase) then
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
        match AuthorityRecord.decode Limits.MaximumRequestBytes data.Request with
        | Ok request when
            request.OperationId = entry.OperationId
            && RecordedAuthorityActs.kind request.Act = act
            && AuthorityRequests.validRecord request
            && Array.isEmpty data.Snapshot
            ->
            let identity =
                match act with
                | AuthorityKind.ProvisionOwner ->
                    target = entry.OperationId && entry.ActorId = target && entry.Sequence = 1L
                | AuthorityKind.Register -> target = entry.OperationId && entry.ActorId <> target
                | _ -> true

            if identity then
                Ok(EntryContent.Authority request)
            else
                Error CoreFault.StoreIntegrityError
        | _ -> Error CoreFault.StoreIntegrityError

    let validate installationId (entry: StoredEntry) =
        if installationId = Guid.Empty || not (validMetadata entry) then
            Error CoreFault.StoreIntegrityError
        else
            let content =
                match entry.Subject, entry.Payload, entry.PayloadNonce with
                | StoredSubject.Case _, None, None -> Ok EntryContent.ErasedCase
                | _ ->
                    payload entry
                    |> Result.bind (fun data ->
                        match entry.Subject with
                        | StoredSubject.Case(id, revision, act, _) ->
                            caseContent installationId entry id revision act data
                        | StoredSubject.Authority(target, act) ->
                            authorityContent entry target act data)

            content
            |> Result.map (fun content ->
                {
                    InstallationId = installationId
                    Entry = entry
                    Content = content
                })

    /// Comparison bytes are canonical; retained ledger payloads remain authored.
    let internal identityBytes validated =
        match validated.Content with
        | EntryContent.Case(request, _) -> Some(Case.canonical request |> RequestRecord.encode)
        | EntryContent.Authority request ->
            Some(AuthorityRequests.canonical request |> AuthorityRecord.encode)
        | EntryContent.ErasedCase -> None

    /// The same snapshot-disclosure policy shapes append, replay, preview, observation and history.
    let private disclosure principal actor id currentRevision revision snapshot =
        if
            ActorAuthorization.can
                principal
                actor
                Capability.ReadHistoryFull
                (ResourceScope.Case id)
            || (currentRevision = revision
                && ActorAuthorization.can
                    principal
                    actor
                    Capability.ReadCase
                    (ResourceScope.Case id))
        then
            SnapshotDisclosure.Disclosed snapshot
        else
            SnapshotDisclosure.Withheld

    let private caseReceipt
        installationId
        principal
        actor
        (current: StoredCase option)
        id
        revision
        act
        content
        =
        match current with
        | None -> Error CoreFault.StoreIntegrityError
        | Some record when
            record.CaseId <> id
            || record.Snapshot.Revision < revision
            || (Case.ofAccepted record.Snapshot |> Result.isError)
            ->
            Error CoreFault.StoreIntegrityError
        | Some record when
            CaseReferenceId.encode installationId (CaseReference.format record.Snapshot.Reference)
            <> id
            ->
            Error CoreFault.StoreIntegrityError
        | Some record ->
            let shown =
                match record.Snapshot.State, content with
                | CaseState.Erased, EntryContent.ErasedCase -> Ok SnapshotDisclosure.Erased
                | CaseState.Erased, EntryContent.Case(_, None) when
                    act = (CaseActKind.Stewardship StewardshipActKind.Erase)
                    ->
                    Ok SnapshotDisclosure.Erased
                | (CaseState.Active _ | CaseState.Voided _), EntryContent.Case(_, Some snapshot) when
                    snapshot.Reference = record.Snapshot.Reference
                    ->
                    match LiveCase.ofState snapshot.State with
                    | None -> Error CoreFault.StoreIntegrityError
                    | Some live ->
                        Ok(disclosure principal actor id record.Snapshot.Revision revision live)
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

    let receipt principal actor current (validated: ValidatedEntry) =
        let entry = validated.Entry

        let subject =
            match entry.Subject, validated.Content with
            | StoredSubject.Case(id, revision, act, _), content ->
                caseReceipt validated.InstallationId principal actor current id revision act content
            | StoredSubject.Authority(target, act), EntryContent.Authority _ ->
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

    let summary (current: StoredCase) (entry: StoredEntry) =
        match entry.Subject with
        | StoredSubject.Case(id, revision, act, _) when
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
    let decide
        installationId
        principal
        actor
        operationId
        bytes
        (existing: StoredEntry option)
        receipt
        =
        match existing with
        | None -> Verdict.Proceed()
        | Some entry when not (LedgerEntries.canObserve principal actor entry.Subject) ->
            Verdict.Refuse Refusal.ResourceUnavailable
        | Some entry when entry.OperationId <> operationId ->
            Verdict.Fail CoreFault.StoreIntegrityError
        | Some entry ->
            match LedgerEntries.validate installationId entry with
            | Error fault -> Verdict.Fail fault
            | Ok validated ->
                match LedgerEntries.identityBytes validated with
                | None -> Verdict.Refuse Refusal.ResourceUnavailable
                | Some accepted when accepted = bytes ->
                    receipt validated
                    |> Result.map Verdict.Replay
                    |> Result.defaultWith Verdict.Fail
                | Some _ -> Verdict.Refuse Refusal.IdempotencyConflict
