namespace ClaimCore.Postgres

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open ClaimCore.Witness

type internal WitnessPruneTarget =
    {
        Sequence: int64
        OperationId: Guid
        Phase: Phase
        Epoch: int64
        EntryHash: byte array
        PayloadHash: byte array
        IsExternalPublication: bool
    }

type internal WitnessPruneSeal =
    {
        CutoffSequence: int64
        CutoffHash: byte array
        TargetCount: int64
        TargetDigest: byte array
    }

/// Page callbacks are tentative until the complete global chain and exact cutoff are verified.
/// The owner must retain a primary transaction uncommitted and roll it back on any later denial.
module internal CaseWitnessPayloadTargets =
    let private invalid () = raise WitnessPending

    let private phaseName =
        function
        | Intent -> "INTENT"
        | SettledAccepted -> "SETTLED_ACCEPTED"
        | SettledRevoked -> "SETTLED_REVOKED"
        | SettledAuthority -> "SETTLED_AUTHORITY"
        | AbortedBeforeCommit -> "ABORTED_BEFORE_COMMIT"
        | KeyRotated -> invalid ()

    let private int64Bytes value =
        let buffer = Array.zeroCreate<byte> 8
        BinaryPrimitives.WriteInt64BigEndian(buffer, value)
        buffer

    let step (previous: byte array) (target: WitnessPruneTarget) =
        Array.concat
            [
                previous
                int64Bytes target.Sequence
                Encoding.ASCII.GetBytes(target.OperationId.ToString("D"))
                Encoding.ASCII.GetBytes(phaseName target.Phase)
                int64Bytes target.Epoch
                target.EntryHash
                target.PayloadHash
                [| if target.IsExternalPublication then 1uy else 0uy |]
            ]
        |> SHA256.HashData

    let private metadataStep previous (ticket: Ticket) =
        Array.concat
            [
                previous
                int64Bytes ticket.Sequence
                Encoding.ASCII.GetBytes(ticket.OperationId.ToString("D"))
                Encoding.ASCII.GetBytes(phaseName ticket.Phase)
                int64Bytes ticket.Epoch
                ticket.EntryHash
                ticket.PayloadHash
            ]
        |> SHA256.HashData

    /// The separate witness cluster can verify only metadata, not owner-private publication
    /// classification. Its fixed v1 digest is rederived after pruning as well as before it.
    let witnessDigest (witness: WitnessProtocol) caseId cutoffSequence cutoffHash =
        let mutable digest =
            Encoding.ASCII.GetBytes("claimcore:witness-prune-targets:v1\000")
            |> SHA256.HashData

        let mutable after = 0L
        let mutable previousHash = Array.zeroCreate<byte> 32
        let mutable count = 0L
        let mutable more = true

        while more do
            let page =
                witness.EvidenceStore.ReadMetadataPage(after, previousHash, cutoffSequence, 32)

            for record in page.Items do
                if record.Ticket.ScopeKind = Case && record.Ticket.SubjectCaseId = Some caseId then
                    digest <- metadataStep digest record.Ticket
                    count <- count + 1L

                after <- record.Ticket.Sequence
                previousHash <- record.Ticket.EntryHash

            more <- page.NextAfter.IsSome

        if after <> cutoffSequence || previousHash <> cutoffHash then
            invalid ()

        count, digest

    let private requireEvidence (witness: WitnessProtocol) (record: MetadataRecord) =
        let ticket = record.Ticket

        if not record.PayloadPresent then
            invalid ()

        let evidence =
            witness.EvidenceStore.TryReadEvidence(ticket.OperationId, ticket.Phase)
            |> Option.defaultWith invalid

        if evidence.Ticket <> ticket then
            invalid ()

        evidence

    let private decrypt (witness: WitnessProtocol) (evidence: Evidence) =
        let ticket = evidence.Ticket

        witness.KeyCustody.Decrypt(
            ticket.KeyId,
            witness.AssociatedData(ticket.OperationId, phaseName ticket.Phase),
            evidence.EncryptedPayload
        )

    let private candidate (witness: WitnessProtocol) (record: MetadataRecord) =
        let ticket = record.Ticket

        let intent =
            witness.EvidenceStore.TryReadEvidence(ticket.OperationId, Intent)
            |> Option.defaultWith invalid

        if
            intent.Ticket.Phase <> Intent
            || intent.Ticket.ScopeKind <> Case
            || intent.Ticket.SubjectCaseId <> ticket.SubjectCaseId
            || intent.Ticket.Sequence > ticket.Sequence
        then
            invalid ()

        let plain = decrypt witness intent

        try
            let marker = CaseWitnessPayloadClassifier.classifyIntent intent.Ticket plain
            SHA256.HashData(plain), marker
        finally
            CryptographicOperations.ZeroMemory(plain)

    let private verifyPayload (witness: WitnessProtocol) (record: MetadataRecord) =
        let expectedCandidate, marker = candidate witness record
        let evidence = requireEvidence witness record
        let plain = decrypt witness evidence

        try
            if
                record.Ticket.Phase <> Intent
                && (plain.Length <> 32 || plain <> expectedCandidate)
            then
                invalid ()
        finally
            CryptographicOperations.ZeroMemory(plain)

        marker

    let private targetOf (record: MetadataRecord) marker =
        let ticket = record.Ticket

        {
            Sequence = ticket.Sequence
            OperationId = ticket.OperationId
            Phase = ticket.Phase
            Epoch = ticket.Epoch
            EntryHash = ticket.EntryHash
            PayloadHash = ticket.PayloadHash
            IsExternalPublication = marker
        }

    let initialDigest () =
        Encoding.ASCII.GetBytes("claimcore:witness-prune-targets:v2\000")
        |> SHA256.HashData

    let scan
        (witness: WitnessProtocol)
        (caseId: Guid)
        cutoffSequence
        (cutoffHash: byte array)
        (emitPage: WitnessPruneTarget list -> unit)
        =
        if
            caseId = Guid.Empty
            || cutoffSequence < 1L
            || cutoffHash.Length <> 32
            || witness.Snapshot().TipSequence < cutoffSequence
        then
            invalid ()

        let mutable digest = initialDigest ()
        let mutable after = 0L
        let mutable previousHash = Array.zeroCreate<byte> 32
        let mutable count = 0L
        let mutable more = true

        while more do
            let page =
                witness.EvidenceStore.ReadMetadataPage(after, previousHash, cutoffSequence, 32)

            let targets = ResizeArray<WitnessPruneTarget>()

            for record in page.Items do
                if record.Ticket.ScopeKind = Case && record.Ticket.SubjectCaseId = Some caseId then
                    let marker = verifyPayload witness record
                    let target = targetOf record marker
                    digest <- step digest target
                    targets.Add(target)
                    count <- count + 1L

                after <- record.Ticket.Sequence
                previousHash <- record.Ticket.EntryHash

            if targets.Count > 0 then
                emitPage (targets |> Seq.toList)

            more <- page.NextAfter.IsSome

        if after <> cutoffSequence || previousHash <> cutoffHash || count < 1L then
            invalid ()

        {
            CutoffSequence = cutoffSequence
            CutoffHash = cutoffHash
            TargetCount = count
            TargetDigest = digest
        }
