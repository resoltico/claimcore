namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
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
    let witnessDigest
        (witness: WitnessProtocol)
        caseId
        cutoffSequence
        cutoffHash
        (ct: CancellationToken)
        =
        task {
            let mutable digest =
                Encoding.ASCII.GetBytes("claimcore:witness-prune-targets:v1\000")
                |> SHA256.HashData

            let mutable after = 0L
            let mutable previousHash = Array.zeroCreate<byte> 32
            let mutable count = 0L
            let mutable more = true

            while more do
                let! page =
                    witness.EvidenceStore.ReadMetadataPage(
                        after,
                        previousHash,
                        cutoffSequence,
                        32,
                        ct
                    )

                for record in page.Items do
                    if
                        record.Ticket.ScopeKind = Case && record.Ticket.SubjectCaseId = Some caseId
                    then
                        digest <- metadataStep digest record.Ticket
                        count <- count + 1L

                    after <- record.Ticket.Sequence
                    previousHash <- record.Ticket.EntryHash

                more <- page.NextAfter.IsSome

            if after <> cutoffSequence || previousHash <> cutoffHash then
                invalid ()

            return count, digest
        }

    let private requireEvidence
        (witness: WitnessProtocol)
        (record: MetadataRecord)
        (ct: CancellationToken)
        =
        task {
            let ticket = record.Ticket

            if not record.PayloadPresent then
                invalid ()

            let! retained =
                witness.EvidenceStore.TryReadEvidence(ticket.OperationId, ticket.Phase, ct)

            let evidence = retained |> Option.defaultWith invalid

            if evidence.Ticket <> ticket then
                invalid ()

            return evidence
        }

    let private decrypt (witness: WitnessProtocol) (evidence: Evidence) =
        let ticket = evidence.Ticket

        witness.KeyCustody.Decrypt(
            ticket.KeyId,
            witness.AssociatedData(ticket.OperationId, phaseName ticket.Phase),
            evidence.EncryptedPayload
        )

    let private candidate
        (witness: WitnessProtocol)
        (record: MetadataRecord)
        (ct: CancellationToken)
        =
        task {
            let ticket = record.Ticket

            let! retained = witness.EvidenceStore.TryReadEvidence(ticket.OperationId, Intent, ct)
            let intent = retained |> Option.defaultWith invalid

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
                return SHA256.HashData(plain), marker
            finally
                CryptographicOperations.ZeroMemory(plain)
        }

    let private verifyPayload
        (witness: WitnessProtocol)
        (record: MetadataRecord)
        (ct: CancellationToken)
        =
        task {
            let! expectedCandidate, marker = candidate witness record ct
            let! evidence = requireEvidence witness record ct
            let plain = decrypt witness evidence

            try
                if
                    record.Ticket.Phase <> Intent
                    && (plain.Length <> 32 || plain <> expectedCandidate)
                then
                    invalid ()
            finally
                CryptographicOperations.ZeroMemory(plain)

            return marker
        }

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

    let private requireCutoff
        (witness: WitnessProtocol)
        caseId
        cutoffSequence
        (cutoffHash: byte array)
        ct
        =
        task {
            let! snapshot = witness.Snapshot(ct)

            if
                caseId = Guid.Empty
                || cutoffSequence < 1L
                || cutoffHash.Length <> 32
                || snapshot.TipSequence < cutoffSequence
            then
                invalid ()

        }

    let scan
        (witness: WitnessProtocol)
        (caseId: Guid)
        cutoffSequence
        (cutoffHash: byte array)
        (emitPage: WitnessPruneTarget list -> Task<unit>)
        (ct: CancellationToken)
        =
        task {
            do! requireCutoff witness caseId cutoffSequence cutoffHash ct

            let mutable digest = initialDigest ()
            let mutable after = 0L
            let mutable previousHash = Array.zeroCreate<byte> 32
            let mutable count = 0L
            let mutable more = true

            while more do
                let! page =
                    witness.EvidenceStore.ReadMetadataPage(
                        after,
                        previousHash,
                        cutoffSequence,
                        32,
                        ct
                    )

                let targets = ResizeArray<WitnessPruneTarget>()

                for record in page.Items do
                    if
                        record.Ticket.ScopeKind = Case && record.Ticket.SubjectCaseId = Some caseId
                    then
                        let! marker = verifyPayload witness record ct
                        let target = targetOf record marker
                        digest <- step digest target
                        targets.Add(target)
                        count <- count + 1L

                    after <- record.Ticket.Sequence
                    previousHash <- record.Ticket.EntryHash

                if targets.Count > 0 then
                    do! emitPage (targets |> Seq.toList)

                more <- page.NextAfter.IsSome

            if after <> cutoffSequence || previousHash <> cutoffHash || count < 1L then
                invalid ()

            return
                {
                    CutoffSequence = cutoffSequence
                    CutoffHash = cutoffHash
                    TargetCount = count
                    TargetDigest = digest
                }
        }
