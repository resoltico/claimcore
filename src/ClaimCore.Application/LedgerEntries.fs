namespace ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal LedgerEntries =
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
            Scope = entry.Scope
            RequestSha256 = Some entry.RequestSha256
            Payload = Some entry.Payload
        }

    let private metadataScope =
        function
        | StoredScope.Authority _ -> LedgerScope.Authority
        | StoredScope.Case(id, revision, act, date) ->
            let encodedAct =
                match act with
                | AcceptedAct.Command kind -> LedgerAct.Command kind
                | AcceptedAct.Stewardship kind -> LedgerAct.Stewardship kind

            LedgerScope.Case
                {
                    CaseId = id
                    Revision = revision
                    Act = encodedAct
                    BusinessDate = date
                    RuleRevision = uint16 DomainRules.version
                }

    let create
        installationId
        (head: LedgerHead)
        instant
        nonce
        actorId
        operationId
        scope
        request
        snapshot
        : EntryDraft =
        if head.Sequence < 0L || head.Sequence = Int64.MaxValue then
            invalidArg (nameof head) "Invalid ledger head."

        let payload =
            LedgerEntry.encodePayload
                {
                    Request = request
                    Snapshot = snapshot
                }

        let commitment = LedgerEntry.commitment nonce payload

        let metadata =
            {
                InstallationId = installationId
                Sequence = uint64 (head.Sequence + 1L)
                PreviousHash = head.EntryHash
                OperationId = operationId
                ActorId = actorId
                RecordedAt = instant
                Scope = metadataScope scope
                PayloadCommitment = commitment
            }

        {
            Sequence = head.Sequence + 1L
            PreviousHash = Array.copy head.EntryHash
            EntryHash = LedgerEntry.hash metadata
            OperationId = operationId
            ActorId = actorId
            RecordedAt = instant
            Scope = scope
            RuleRevision =
                (match scope with
                 | StoredScope.Case _ -> Some DomainRules.version
                 | _ -> None)
            PayloadCommitment = commitment
            PayloadNonce = Array.copy nonce
            Payload = payload
            RequestSha256 = LedgerEntry.requestDigest request
        }

    let payload (entry: StoredEntry) =
        match entry.Payload, entry.RequestSha256 with
        | Some bytes, Some digest ->
            match LedgerEntry.decodePayload 262144 bytes with
            | Ok value when LedgerEntry.requestDigest value.Request = digest -> Ok value
            | Ok _ -> Error CoreFault.OperationContentConflict
            | Error _ -> Error CoreFault.StoreIntegrityError
        | _ -> Error CoreFault.StoreIntegrityError

    let canObserve principal actor scope =
        match scope with
        | StoredScope.Case(id, _, _, _) ->
            ActorAuthorization.can
                principal
                actor
                Capability.ObserveOperation
                (ResourceScope.Case id)
        | StoredScope.Authority _ ->
            ActorAuthorization.can
                principal
                actor
                Capability.ManageAuthority
                ResourceScope.Installation

    let private requestIdentity act bytes =
        match act with
        | AcceptedAct.Command _ ->
            RequestRecord.decode 65536 bytes
            |> Result.map (fun request ->
                request.CaseReference,
                request.OperationId,
                request.ExpectedVersion,
                AcceptedAct.Command(Commands.kind request.Command))
        | AcceptedAct.Stewardship _ ->
            StewardshipRecord.decode 65536 bytes
            |> Result.map (fun request ->
                request.CaseReference,
                request.OperationId,
                request.ExpectedVersion,
                AcceptedAct.Stewardship(Stewardship.kind request.Action))

    let private snapshot act reference revision bytes =
        if act = AcceptedAct.Stewardship StewardshipKind.Erase then
            if Array.isEmpty bytes then
                Ok None
            else
                Error CoreFault.StoreIntegrityError
        else
            match ClaimCore.RecordFormat.CaseRecord.decodeSnapshot bytes with
            | Ok view when
                view.Version = revision
                && view.Fields.CaseReference = reference
                && (Claim.restore view |> Result.isOk)
                ->
                Ok(Some view)
            | _ -> Error CoreFault.StoreIntegrityError

    let receipt (entry: StoredEntry) =
        match entry.Scope, payload entry with
        | StoredScope.Case(_, revision, act, _), Ok data ->
            match requestIdentity act data.Request with
            | Ok(reference, id, expected, decodedAct) when
                id = entry.OperationId && expected = revision - 1L && decodedAct = act
                ->
                snapshot act reference revision data.Snapshot
                |> Result.map (fun view ->
                    {
                        OperationId = id
                        Position = position entry.Sequence entry.EntryHash
                        CaseReference = reference
                        Revision = revision
                        Act = act
                        Snapshot = view
                        Disposition =
                            (match act with
                             | AcceptedAct.Stewardship StewardshipKind.Erase -> None
                             | AcceptedAct.Stewardship StewardshipKind.Void ->
                                 Some CaseDisposition.Voided
                             | _ -> Some CaseDisposition.Active)
                        RecordedAt = entry.RecordedAt
                        RecordedBy = entry.ActorId
                    })
            | _ -> Error CoreFault.StoreIntegrityError
        | _, Error fault -> Error fault
        | _ -> Error CoreFault.StoreIntegrityError

[<RequireQualifiedAccess>]
type internal IdentityDecision =
    | Fresh
    | Replay of StoredEntry
    | Refuse of Rejection
    | Fail of CoreFault

module internal OperationIdentity =
    let decide principal actor bytes (existing: StoredEntry option) =
        match existing with
        | None -> IdentityDecision.Fresh
        | Some entry when
            not (LedgerEntries.canObserve principal actor entry.Scope)
            || entry.Payload.IsNone
            ->
            IdentityDecision.Refuse Rejection.ResourceUnavailable
        | Some entry ->
            match LedgerEntries.payload entry with
            | Error fault -> IdentityDecision.Fail fault
            | Ok payload when payload.Request = bytes -> IdentityDecision.Replay entry
            | Ok _ -> IdentityDecision.Refuse Rejection.IdempotencyConflict
