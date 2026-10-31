namespace ClaimCore.RecordFormat


open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open ClaimCore.Domain

type CaseEntryMetadata =
    {
        CaseId: CaseId
        Revision: Revision
        Act: CaseActKind
        BusinessDate: DateOnly
    }

type AuthorityEntryMetadata =
    {
        TargetActorId: ActorId
        Act: AuthorityKind
    }

[<RequireQualifiedAccess>]
type RecordedTarget =
    | NonAcceptance of OperationId
    | Case of CaseEntryMetadata
    | Authority of AuthorityEntryMetadata

type LedgerMetadata =
    {
        InstallationId: Guid
        Sequence: Sequence
        PreviousHash: byte array
        OperationId: OperationId
        ActorId: ActorId
        RecordedAt: DateTimeOffset
        Subject: RecordedTarget
        CreationReference: CaseReference option
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

    type private SubjectEncoding =
        {
            Kind: byte
            Act: byte
            Identifier: byte array
            Revision: byte array
            Date: byte array
        }

    let private subjectBytes =
        function
        | RecordedTarget.NonAcceptance operation ->
            {
                Kind = 3uy
                Act = 64uy
                Identifier = (OperationId.value operation).ToByteArray(bigEndian = true)
                Revision = i64 0L
                Date = i32 0
            }
        | RecordedTarget.Authority subject ->
            {
                Kind = 2uy
                Act = authorityCode subject.Act
                Identifier = (ActorId.value subject.TargetActorId).ToByteArray(bigEndian = true)
                Revision = i64 0L
                Date = i32 0
            }
        | RecordedTarget.Case subject ->
            if subject.Revision < Revision.first then
                invalidArg (nameof subject) "Invalid case ledger metadata."

            {
                Kind = 1uy
                Act = actCode subject.Act
                Identifier = (CaseId.value subject.CaseId).ToByteArray(bigEndian = true)
                Revision = i64 (Revision.value subject.Revision)
                Date = i32 subject.BusinessDate.DayNumber
            }

    /// Fixed metadata plus a presence tag and length-framed retained OPEN reference.
    let preimage (metadata: LedgerMetadata) =
        requireHash DurableNames.PreviousHash metadata.PreviousHash
        requireHash DurableNames.PayloadCommitment metadata.PayloadCommitment

        if metadata.InstallationId = Guid.Empty || metadata.Sequence < Sequence.first then
            invalidArg (nameof metadata) "Invalid ledger identity or sequence."

        let ticks = metadata.RecordedAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks

        if not (RecordedInstant.hasMicrosecondPrecision metadata.RecordedAt) then
            invalidArg (nameof metadata) "Use microsecond precision."

        let subject = subjectBytes metadata.Subject

        Array.concat
            [
                Encoding.ASCII.GetBytes(
                    sprintf "claimcore/ledger-entry/%d\n" RecordVersions.LedgerEntry
                )
                metadata.InstallationId.ToByteArray(bigEndian = true)
                u64 (uint64 (Sequence.value metadata.Sequence))
                metadata.PreviousHash
                [| subject.Kind; subject.Act |]
                (OperationId.value metadata.OperationId).ToByteArray(bigEndian = true)
                (ActorId.value metadata.ActorId).ToByteArray(bigEndian = true)
                i64 (ticks / TimeSpan.TicksPerMicrosecond)
                subject.Identifier
                subject.Revision
                subject.Date
                // Length framing makes retained variable-width identity unambiguous.
                (match metadata.CreationReference with
                 | None -> [| 0uy |]
                 | Some reference ->
                     let bytes = Encoding.UTF8.GetBytes(CaseReference.format reference)
                     Array.concat [ [| 1uy |]; u32 (uint32 bytes.Length); bytes ])
                metadata.PayloadCommitment
            ]

    let hash metadata = preimage metadata |> SHA256.HashData
