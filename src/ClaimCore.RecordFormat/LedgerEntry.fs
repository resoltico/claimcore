namespace ClaimCore.RecordFormat

open ClaimCore.Contract

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain

type CaseEntryMetadata =
    {
        CaseId: Guid
        Revision: int64
        Act: CaseActKind
        BusinessDate: DateOnly
    }

type AuthorityEntryMetadata =
    {
        TargetActorId: Guid
        Act: RecordedAuthorityKind
    }

[<RequireQualifiedAccess>]
type LedgerScope =
    | Case of CaseEntryMetadata
    | Authority of AuthorityEntryMetadata

type LedgerMetadata =
    {
        InstallationId: Guid
        Sequence: int64
        PreviousHash: byte array
        OperationId: Guid
        ActorId: Guid
        RecordedAt: DateTimeOffset
        Scope: LedgerScope
        PayloadCommitment: byte array
    }

type LedgerPayload =
    {
        Request: byte array
        Snapshot: byte array
    }

module LedgerEntry =
    let private u64 value =
        let bytes = Array.zeroCreate<byte> 8
        BinaryPrimitives.WriteUInt64BigEndian(Span bytes, value)
        bytes

    let private i64 value =
        let bytes = Array.zeroCreate<byte> 8
        BinaryPrimitives.WriteInt64BigEndian(Span bytes, value)
        bytes

    let private i32 value =
        let bytes = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteInt32BigEndian(Span bytes, value)
        bytes

    let private u32 value =
        let bytes = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteUInt32BigEndian(Span bytes, value)
        bytes

    let private caseActCodes =
        Map.ofList
            [
                (CaseActKind.Business BusinessActKind.Open), 1uy
                (CaseActKind.Business BusinessActKind.AmendRegistration), 2uy
                (CaseActKind.Business BusinessActKind.CorrectCase), 3uy
                (CaseActKind.Business BusinessActKind.Decide), 4uy
                (CaseActKind.Business BusinessActKind.WithdrawDecision), 5uy
                (CaseActKind.Business BusinessActKind.RecordPayment), 6uy
                (CaseActKind.Business BusinessActKind.ClearPayment), 7uy
                (CaseActKind.Business BusinessActKind.Close), 8uy
                (CaseActKind.Business BusinessActKind.Reopen), 9uy
                (CaseActKind.Stewardship StewardshipActKind.Void), 10uy
                (CaseActKind.Stewardship StewardshipActKind.Reinstate), 11uy
                (CaseActKind.Stewardship StewardshipActKind.Erase), 12uy
            ]

    let actCode act = caseActCodes[act]

    let private authorityCode =
        function
        | RecordedAuthorityKind.ProvisionOwner -> 32uy
        | RecordedAuthorityKind.RegisterActor -> 33uy
        | RecordedAuthorityKind.SetGrant -> 34uy
        | RecordedAuthorityKind.SetEnabled -> 35uy

    let encodePayload (payload: LedgerPayload) =
        Array.concat
            [
                u32 (uint32 payload.Request.Length)
                payload.Request
                u32 (uint32 payload.Snapshot.Length)
                payload.Snapshot
            ]

    let decodePayload maxBytes (bytes: byte array) =
        let refusal () =
            Error
                {
                    Field = "$"
                    Message = "Invalid ledger payload framing or size."
                }

        if obj.ReferenceEquals(bytes, null) || bytes.Length < 8 || bytes.Length > maxBytes then
            refusal ()
        else
            let requestLength = BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan(bytes, 0, 4))

            if requestLength > uint32 (bytes.Length - 8) then
                refusal ()
            else
                let offset = 4 + int requestLength

                let snapshotLength =
                    BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan(bytes, offset, 4))

                if snapshotLength <> uint32 (bytes.Length - offset - 4) then
                    refusal ()
                else
                    Ok
                        {
                            Request = bytes[4 .. offset - 1]
                            Snapshot = bytes[offset + 4 ..]
                        }

    let private requireHash name (bytes: byte array) =
        if obj.ReferenceEquals(bytes, null) || bytes.Length <> 32 then
            invalidArg name "Use exactly 32 bytes."

    let commitment nonce payload =
        requireHash (nameof nonce) nonce
        SHA256.HashData(Array.append nonce payload)

    let requestDigest (request: byte array) = SHA256.HashData request

    let private scopeBytes =
        function
        | LedgerScope.Authority scope ->
            if scope.TargetActorId = Guid.Empty then
                invalidArg (nameof scope) "Invalid authority subject."

            2uy, authorityCode scope.Act, scope.TargetActorId, 0L, 0
        | LedgerScope.Case scope ->
            if
                scope.CaseId = Guid.Empty
                || scope.Revision < 1L
                || scope.Revision > Revision.Maximum
            then
                invalidArg (nameof scope) "Invalid case ledger metadata."

            1uy, actCode scope.Act, scope.CaseId, scope.Revision, scope.BusinessDate.DayNumber

    /// Exactly 183 bytes. Only the salted payload commitment covers erasable business content.
    let preimage (metadata: LedgerMetadata) =
        requireHash "previousHash" metadata.PreviousHash
        requireHash "payloadCommitment" metadata.PayloadCommitment

        if
            metadata.InstallationId = Guid.Empty
            || metadata.OperationId = Guid.Empty
            || metadata.ActorId = Guid.Empty
            || metadata.Sequence < 1L
            || metadata.Sequence > Revision.Maximum
        then
            invalidArg (nameof metadata) "Invalid ledger identity or sequence."

        let ticks = metadata.RecordedAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks

        if ticks % 10L <> 0L then
            invalidArg (nameof metadata) "Use microsecond precision."

        let scope, act, subject, revision, date = scopeBytes metadata.Scope

        Array.concat
            [
                Encoding.ASCII.GetBytes "claimcore/ledger-entry/2\n"
                metadata.InstallationId.ToByteArray(bigEndian = true)
                u64 (uint64 metadata.Sequence)
                metadata.PreviousHash
                [| scope; act |]
                metadata.OperationId.ToByteArray(bigEndian = true)
                metadata.ActorId.ToByteArray(bigEndian = true)
                i64 (ticks / 10L)
                subject.ToByteArray(bigEndian = true)
                i64 revision
                i32 date
                metadata.PayloadCommitment
            ]

    let hash metadata = preimage metadata |> SHA256.HashData
