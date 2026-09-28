namespace ClaimCore.Database

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Postgres

[<NoEquality; NoComparison>]
type internal FencedTailVerification =
    {
        Scope: string
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        HandoffId: Guid
        PublicationManifestSha256: string
        ReportSha256: string
        FenceReportSha256: string
        SupplementSha256: string
        FinalWalObjectSha256: string
        W1Sequence: int64
        W1Hash: string
        FinalWalObjects: int
        IndependentProbeSha256: string
        ProbeEvidenceSha256: string
        CheckpointSignerKeyId: Guid
        CheckpointHolderActorId: Guid
        SignedReport: byte array
        ReportSignature: byte array
        SignedFence: byte array
        FenceSignature: byte array
        SignedSupplement: byte array
        SupplementSignature: byte array
        ValidUntil: DateTimeOffset
        RealDataReady: bool
    }

/// Rechecks a post-W1 signed tail against current signer/holder, witness, primary and private files.
/// The caller must first independently verify the pre-W1 report and off-host probe publication.
module internal DatabaseRestoreFencedTailVerification =
    let private digest (bytes: byte array) =
        SHA256.HashData(ReadOnlySpan<byte>(bytes)) |> Convert.ToHexStringLower

    let private reportBound
        (report: RestoreReportClaims)
        (index: RestoreEvidenceIndex)
        (tail: FencedTailClaims)
        reportSha
        fenceSha
        =
        if
            tail.Scope <> report.Scope
            || tail.InstallationId <> report.InstallationId
            || tail.LineageId <> report.LineageId
            || tail.Epoch <> report.Epoch
            || tail.OldGeneration < 1L
            || tail.NewGeneration <> tail.OldGeneration + 1L
            || tail.W1Sequence <= report.WitnessCutoff
            || tail.ReportSha256 <> reportSha
            || tail.FenceReportSha256 <> fenceSha
            || tail.CheckpointSignerKeyId <> index.CheckpointSignerKeyId
            || tail.PrimaryRegisteredWalHorizon <> report.PrimaryRegisteredWalHorizon
            || tail.WitnessRegisteredWalHorizon <> report.WitnessRegisteredWalHorizon
        then
            invalidOp "Fenced recovery tail differs from the signed pre-W1 report."

    let private fenceBound
        (fence: WriterFenceClaims)
        (tail: FencedTailClaims)
        reportSha
        expectedProbeSha
        =
        if
            fence.InstallationId <> tail.InstallationId
            || fence.LineageId <> tail.LineageId
            || fence.Epoch <> tail.Epoch
            || fence.OldGeneration <> tail.OldGeneration
            || fence.NewGeneration <> tail.NewGeneration
            || fence.ReportSha256 <> reportSha
            || fence.CheckpointSignerKeyId <> tail.CheckpointSignerKeyId
            || fence.IndependentProbeSha256 <> expectedProbeSha
            || fence.CheckedAt > tail.CheckedAt
            || fence.ValidUntil < tail.CheckedAt
        then
            invalidOp "Fenced recovery tail and independent writer evidence diverge."

    let private bound
        report
        index
        fence
        tail
        expectedProbeSha
        (evidence: SignedFencedTailEvidence)
        =
        let reportSha = digest evidence.Report
        reportBound report index tail reportSha (digest evidence.Fence)
        fenceBound fence tail reportSha expectedProbeSha

    let private proof
        (tail: FencedTailClaims)
        (fence: WriterFenceClaims)
        (index: RestoreEvidenceIndex)
        checkpointHolder
        probeEvidenceSha
        (evidence: SignedFencedTailEvidence)
        =
        {
            Scope = tail.Scope
            InstallationId = tail.InstallationId
            LineageId = tail.LineageId
            Epoch = tail.Epoch
            WriterGeneration = tail.NewGeneration
            HandoffId = tail.HandoffId
            PublicationManifestSha256 = index.PublicationManifestSha256
            ReportSha256 = tail.ReportSha256
            FenceReportSha256 = tail.FenceReportSha256
            SupplementSha256 = digest evidence.Supplement
            FinalWalObjectSha256 = DatabaseRestoreWalObjectDigest.compute tail.WalObjects
            W1Sequence = tail.W1Sequence
            W1Hash = tail.W1Hash
            FinalWalObjects = tail.WalObjects.Length
            IndependentProbeSha256 = fence.IndependentProbeSha256
            ProbeEvidenceSha256 = probeEvidenceSha
            CheckpointSignerKeyId = tail.CheckpointSignerKeyId
            CheckpointHolderActorId = checkpointHolder
            SignedReport = Array.copy evidence.Report
            ReportSignature = Array.copy evidence.ReportSignature
            SignedFence = Array.copy evidence.Fence
            FenceSignature = Array.copy evidence.FenceSignature
            SignedSupplement = Array.copy evidence.Supplement
            SupplementSignature = Array.copy evidence.SupplementSignature
            ValidUntil = min tail.ValidUntil fence.ValidUntil
            RealDataReady = false
        }

    let private requireShapes (evidence: SignedFencedTailEvidence) =
        if
            evidence.Report.Length < 2
            || evidence.Report.Length > 131072
            || evidence.ReportSignature.Length <> 64
            || evidence.Fence.Length < 2
            || evidence.Fence.Length > 32768
            || evidence.FenceSignature.Length <> 64
            || evidence.Supplement.Length < 2
            || evidence.Supplement.Length > 8388608
            || evidence.SupplementSignature.Length <> 64
        then
            invalidOp "Signed fenced-tail evidence exceeds reviewed bounds."

    let private verifyUsing
        historical
        (owner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (report: RestoreReportClaims)
        (index: RestoreEvidenceIndex)
        expectedProbeSha
        probeEvidenceSha
        configuredArchiveRoot
        (evidence: SignedFencedTailEvidence)
        now
        =
        requireShapes evidence

        let fence =
            DatabaseRestoreWriterFenceClaims.parse evidence.Fence now
            |> Option.defaultWith (fun () -> invalidOp "Writer fence document is invalid.")

        let tail =
            DatabaseRestoreFencedTailClaims.parse evidence.Supplement now
            |> Option.defaultWith (fun () -> invalidOp "Fenced tail document is invalid.")

        bound report index fence tail expectedProbeSha evidence

        let checkpointHolder =
            DatabaseRestoreFencedTailSignatures.holder
                historical
                owner
                transaction
                witness
                report
                index
                tail
                evidence

        DatabaseRestoreFencedTailLive.primaryW1 owner transaction tail

        match witness.TryReadHashAtSequence(tail.W1Sequence) with
        | Some hash when Convert.ToHexStringLower(hash) = tail.W1Hash -> ()
        | _ -> invalidOp "Independent witness W1 settlement is missing or changed."

        let segments =
            tail.WalObjects
            |> List.map (fun item -> item.Cluster, item.Segment, item.SegmentBytes)

        DatabaseRestoreWalCoverage.verifyFencedTail
            report
            tail.PrimaryFinalWalEndpoint
            tail.WitnessFinalWalEndpoint
            segments

        if configuredArchiveRoot <> tail.ArchiveRoot then
            invalidOp "Historical final WAL archive identity changed."

        if not historical then
            DatabaseRestoreFencedTailLive.archiveObjects configuredArchiveRoot tail

        proof tail fence index checkpointHolder probeEvidenceSha evidence

    let verify
        owner
        transaction
        witness
        report
        index
        expectedProbeSha
        probeEvidenceSha
        configuredArchiveRoot
        evidence
        now
        =
        verifyUsing
            false
            owner
            transaction
            witness
            report
            index
            expectedProbeSha
            probeEvidenceSha
            configuredArchiveRoot
            evidence
            now

    let verifyHistorical
        owner
        transaction
        witness
        report
        index
        expectedProbeSha
        probeEvidenceSha
        configuredArchiveRoot
        evidence
        atIssuance
        =
        verifyUsing
            true
            owner
            transaction
            witness
            report
            index
            expectedProbeSha
            probeEvidenceSha
            configuredArchiveRoot
            evidence
            atIssuance
