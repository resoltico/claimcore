namespace ClaimCore.RecordFormat


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
        Act: AuthorityKind
    }

[<RequireQualifiedAccess>]
type RecordedTarget =
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
        Subject: RecordedTarget
        PayloadCommitment: byte array
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

    let actCode =
        function
        | CaseActKind.Open -> 1uy
        | CaseActKind.AmendRegistration -> 2uy
        | CaseActKind.Correct -> 3uy
        | CaseActKind.Decide -> 4uy
        | CaseActKind.WithdrawDecision -> 5uy
        | CaseActKind.RecordPayment -> 6uy
        | CaseActKind.ClearPayment -> 7uy
        | CaseActKind.Close -> 8uy
        | CaseActKind.Reopen -> 9uy
        | CaseActKind.Void -> 10uy
        | CaseActKind.Reinstate -> 11uy
        | CaseActKind.Erase -> 12uy

    let private authorityCode =
        function
        | AuthorityKind.ProvisionOwner -> 32uy
        | AuthorityKind.Register -> 33uy
        | AuthorityKind.Grant -> 34uy
        | AuthorityKind.Revoke -> 35uy
        | AuthorityKind.Enable -> 36uy
        | AuthorityKind.Disable -> 37uy

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

    let private subjectBytes =
        function
        | RecordedTarget.Authority subject ->
            if subject.TargetActorId = Guid.Empty then
                invalidArg (nameof subject) "Invalid authority subject."

            2uy, authorityCode subject.Act, subject.TargetActorId, 0L, 0
        | RecordedTarget.Case subject ->
            if
                subject.CaseId = Guid.Empty
                || subject.Revision < 1L
                || subject.Revision > Revision.Maximum
            then
                invalidArg (nameof subject) "Invalid case ledger metadata."

            1uy,
            actCode subject.Act,
            subject.CaseId,
            subject.Revision,
            subject.BusinessDate.DayNumber

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

        let subjectKind, act, subjectId, revision, date = subjectBytes metadata.Subject

        Array.concat
            [
                Encoding.ASCII.GetBytes(
                    sprintf "claimcore/ledger-entry/%d\n" RecordVersions.LedgerEntry
                )
                metadata.InstallationId.ToByteArray(bigEndian = true)
                u64 (uint64 metadata.Sequence)
                metadata.PreviousHash
                [| subjectKind; act |]
                metadata.OperationId.ToByteArray(bigEndian = true)
                metadata.ActorId.ToByteArray(bigEndian = true)
                i64 (ticks / 10L)
                subjectId.ToByteArray(bigEndian = true)
                i64 revision
                i32 date
                metadata.PayloadCommitment
            ]

    let hash metadata = preimage metadata |> SHA256.HashData
