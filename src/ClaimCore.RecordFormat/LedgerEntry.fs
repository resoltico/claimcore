namespace ClaimCore.RecordFormat

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain

[<RequireQualifiedAccess>]
type LedgerAct =
    | Command of CommandKind
    | Stewardship of StewardshipKind

type CaseEntryMetadata =
    {
        CaseId: Guid
        Revision: int64
        Act: LedgerAct
        BusinessDate: DateOnly
        RuleRevision: uint16
    }

[<RequireQualifiedAccess>]
type LedgerScope =
    | Case of CaseEntryMetadata
    | Authority

type LedgerMetadata =
    {
        InstallationId: Guid
        Sequence: uint64
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

    let private u16 value =
        let bytes = Array.zeroCreate<byte> 2
        BinaryPrimitives.WriteUInt16BigEndian(Span bytes, value)
        bytes

    let actCode =
        function
        | LedgerAct.Command CommandKind.Open -> 1uy
        | LedgerAct.Command CommandKind.AmendRegistration -> 2uy
        | LedgerAct.Command CommandKind.CorrectCase -> 3uy
        | LedgerAct.Command CommandKind.Decide -> 4uy
        | LedgerAct.Command CommandKind.WithdrawDecision -> 5uy
        | LedgerAct.Command CommandKind.RecordPayment -> 6uy
        | LedgerAct.Command CommandKind.ClearPayment -> 7uy
        | LedgerAct.Command CommandKind.Close -> 8uy
        | LedgerAct.Command CommandKind.Reopen -> 9uy
        | LedgerAct.Stewardship StewardshipKind.Void -> 10uy
        | LedgerAct.Stewardship StewardshipKind.Reinstate -> 11uy
        | LedgerAct.Stewardship StewardshipKind.Erase -> 12uy

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
        | LedgerScope.Authority -> 2uy, Guid.Empty, 0L, 0uy, 0, 0us
        | LedgerScope.Case scope ->
            if
                scope.CaseId = Guid.Empty
                || scope.Revision < 1L
                || scope.Revision >= Int64.MaxValue
                || scope.RuleRevision = 0us
            then
                invalidArg (nameof scope) "Invalid case ledger metadata."

            1uy,
            scope.CaseId,
            scope.Revision,
            actCode scope.Act,
            scope.BusinessDate.DayNumber,
            scope.RuleRevision

    /// Exactly 185 bytes. Payload, nonce and request digest are deliberately absent.
    let preimage (metadata: LedgerMetadata) =
        requireHash "previousHash" metadata.PreviousHash
        requireHash "payloadCommitment" metadata.PayloadCommitment

        if
            metadata.InstallationId = Guid.Empty
            || metadata.OperationId = Guid.Empty
            || metadata.ActorId = Guid.Empty
            || metadata.Sequence = 0UL
        then
            invalidArg (nameof metadata) "Ledger identity and sequence must be non-empty."

        let ticks = metadata.RecordedAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks

        if ticks % 10L <> 0L then
            invalidArg (nameof metadata) "The recorded instant must have microsecond precision."

        let kind, caseId, revision, act, date, rules = scopeBytes metadata.Scope

        Array.concat
            [
                Encoding.ASCII.GetBytes $"claimcore/ledger-entry/{RecordVersions.LedgerEntry}\n"
                metadata.InstallationId.ToByteArray(bigEndian = true)
                u64 metadata.Sequence
                metadata.PreviousHash
                [| kind |]
                metadata.OperationId.ToByteArray(bigEndian = true)
                metadata.ActorId.ToByteArray(bigEndian = true)
                i64 (ticks / 10L)
                caseId.ToByteArray(bigEndian = true)
                i64 revision
                [| act |]
                i32 date
                u16 rules
                metadata.PayloadCommitment
            ]

    let hash metadata = preimage metadata |> SHA256.HashData
