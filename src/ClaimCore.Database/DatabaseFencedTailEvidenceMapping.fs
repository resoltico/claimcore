namespace ClaimCore.Database

open System
open ClaimCore.Postgres

/// Maps only a fully rechecked signed tail to the exact activation candidate.
/// Independent-host qualification remains a separate verifier-owned result.
module internal DatabaseFencedTailEvidenceMapping =
    let activation (proof: FencedTailVerification) : WriterActivationEvidence =
        {
            HandoffId = proof.HandoffId
            InstallationId = proof.InstallationId
            LineageId = proof.LineageId
            Epoch = proof.Epoch
            WriterGeneration = proof.WriterGeneration
            W1Sequence = proof.W1Sequence
            W1Hash = Convert.FromHexString(proof.W1Hash)
            PublicationManifestSha256 = Convert.FromHexString(proof.PublicationManifestSha256)
            ReportSha256 = Convert.FromHexString(proof.ReportSha256)
            FenceSha256 = Convert.FromHexString(proof.FenceReportSha256)
            SupplementSha256 = Convert.FromHexString(proof.SupplementSha256)
            FinalWalObjectSha256 = Convert.FromHexString(proof.FinalWalObjectSha256)
            FinalWalObjectCount = proof.FinalWalObjects
            IndependentProbeSha256 = Convert.FromHexString(proof.IndependentProbeSha256)
            ProbeEvidenceSha256 = Convert.FromHexString(proof.ProbeEvidenceSha256)
            CheckpointSigningKeyId = proof.CheckpointSignerKeyId
            CheckpointHolderActorId = proof.CheckpointHolderActorId
            SignedReport = Array.copy proof.SignedReport
            ReportSignature = Array.copy proof.ReportSignature
            SignedFence = Array.copy proof.SignedFence
            FenceSignature = Array.copy proof.FenceSignature
            SignedSupplement = Array.copy proof.SignedSupplement
            SupplementSignature = Array.copy proof.SupplementSignature
            ValidUntil = proof.ValidUntil
        }
