module internal ClaimCore.IntegrationTests.RestoreWriterHandoffTypedSettlement

open System.Threading
open System
open System.Security.Cryptography
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RestorePhysicalConnections
open ClaimCore.IntegrationTests.RestoreWriterHandoffQualificationFixture
open ClaimCore.IntegrationTests.WriterHandoffDocuments
open ClaimCore.TestSupport

let private prepared (owner: NpgsqlConnection) (witness: WitnessProtocol) handoffId =
    use transaction = owner.BeginTransaction()

    let value =
        WriterHandoffOwnerRead.preparation
            owner
            transaction
            witness
            handoffId
            CancellationToken.None
        |> await
        |> Option.defaultWith (fun () -> failtest "Confirmed W1 PREPARE row is absent")

    transaction.Rollback()
    value

let private signedSettlement
    (witness: WitnessProtocol)
    (proposal: WriterHandoffPreparation)
    (prepare: byte array)
    (checkpointKey: Key)
    validUntil
    =
    let intent =
        (witness.EvidenceStore
            .TryReadEvidence(proposal.HandoffId, Intent, CancellationToken.None)
            .GetAwaiter()
            .GetResult())
        |> Option.map _.Ticket
        |> Option.defaultWith (fun () -> failtest "W1 PREPARE intent is absent")

    let canonical =
        WriterHandoffDocuments.settlement
            witness.Identity
            proposal.HandoffId
            proposal.OldGeneration
            intent.Sequence
            intent.EntryHash
            (SHA256.HashData(prepare))
            proposal.NewCapabilitySha256
            proposal.CheckpointSigningKeyId
            proposal.FenceReportSha256
            proposal.InventorySha256
            proposal.RestoreReportSha256
            proposal.ApprovalOneId
            proposal.ApprovalTwoId
            validUntil

    canonical, SignatureAlgorithm.Ed25519.Sign(checkpointKey, canonical)

let private qualifiedSettlement
    owner
    (witness: WitnessProtocol)
    (access: RestoredPairAccess)
    custody
    suppression
    (input: RestoreProduceInput)
    (produced: SignedRestoreProduction)
    (proposal: WriterHandoffPreparation)
    canonical
    fence
    fenceSignature
    =
    let saved = prepared owner witness proposal.HandoffId

    let files: RestoreReportFiles =
        {
            Report = produced.Evidence.Report
            Signature = produced.ReportSignature
            EvidenceIndex = produced.Evidence.EvidenceIndex
            ReportSha256 = produced.Evidence.ReportSha256
            EvidenceIndexSha256 = produced.Evidence.EvidenceIndexSha256
        }

    DatabaseWriterHandoffQualification.settlement
        input.Publication
        "synthetic-only"
        access.Owner
        access.WitnessAudit
        access.WitnessOwner
        custody
        suppression
        files
        saved
        canonical
        fence
        fenceSignature
        input.Publication.VerifierBinarySha256
        DateTimeOffset.UtcNow
    |> await

let private commit
    owner
    source
    (access: RestoredPairAccess)
    (witness: WitnessProtocol)
    qualifier
    commitments
    (proposal: WriterHandoffPreparation)
    canonical
    signature
    oldBytes
    newCapability
    =
    match
        WriterHandoffOwnerSettlement.commit
            owner
            source
            access.WitnessOwner
            witness
            qualifier
            (Some commitments)
            canonical
            signature
            oldBytes
            newCapability
        |> await
    with
    | WriterHandoffOwnerOutcome.Settled(id, sequence, hash) when id = proposal.HandoffId ->
        Expect.equal
            ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipSequence)
            sequence
            "Exact W1 witness sequence"

        Expect.equal
            ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult()).TipHash)
            hash
            "Exact W1 witness hash"

        id, sequence, hash
    | _ -> failtest "Typed synthetic W1 SETTLE did not complete"

let finish
    owner
    source
    (access: RestoredPairAccess)
    (witness: WitnessProtocol)
    custody
    suppression
    (input: RestoreProduceInput)
    (produced: SignedRestoreProduction)
    (checkpointKey: Key)
    commitments
    (proposal: WriterHandoffPreparation)
    prepare
    fence
    fenceSignature
    oldBytes
    newCapability
    validUntil
    =
    let canonical, signature =
        signedSettlement witness proposal prepare checkpointKey validUntil

    let qualifier =
        qualifiedSettlement
            owner
            witness
            access
            custody
            suppression
            input
            produced
            proposal
            canonical
            fence
            fenceSignature

    commit
        owner
        source
        access
        witness
        qualifier
        commitments
        proposal
        canonical
        signature
        oldBytes
        newCapability
