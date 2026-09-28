module internal ClaimCore.IntegrationTests.WriterHandoffSyntheticVerifier

open System.Security.Cryptography
open System.Threading.Tasks
open ClaimCore.Postgres

/// Test-only evidence injector: exercises owner protocol mechanics, never production trust.
let create (reviewed: WriterHandoffPreparation) : IWriterHandoffEvidenceVerifier =
    let proof
        installation
        lineage
        epoch
        generation
        cutoff
        hash
        newCapability
        report
        fence
        inventory
        key
        until
        =
        {
            PublicationManifestSha256 = SHA256.HashData(Array.create 32 0x79uy)
            InstallationId = installation
            LineageId = lineage
            Epoch = epoch
            OldGeneration = generation
            ReviewedCutoffSequence = cutoff
            ReviewedCutoffHash = hash
            NewCapabilitySha256 = newCapability
            RestoreReportSha256 = report
            FenceReportSha256 = fence
            InventorySha256 = inventory
            CheckpointSigningKeyId = key
            ValidUntil = until
            IndependentProbeNonceSha256 = SHA256.HashData(Array.create 32 0x7Auy)
        }

    { new IWriterHandoffEvidenceVerifier with
        member _.VerifyPreparation(proposal, _, _) =
            proof
                proposal.InstallationId
                proposal.LineageId
                proposal.Epoch
                proposal.OldGeneration
                proposal.ReviewedCutoffSequence
                proposal.ReviewedCutoffHash
                proposal.NewCapabilitySha256
                proposal.RestoreReportSha256
                proposal.FenceReportSha256
                proposal.InventorySha256
                proposal.CheckpointSigningKeyId
                proposal.ValidUntil
            |> Some
            |> Task.FromResult

        member _.VerifySettlement(proposal, _, _) =
            proof
                proposal.InstallationId
                proposal.LineageId
                proposal.Epoch
                proposal.OldGeneration
                reviewed.ReviewedCutoffSequence
                reviewed.ReviewedCutoffHash
                proposal.NewCapabilitySha256
                proposal.RestoreReportSha256
                proposal.FenceReportSha256
                proposal.InventorySha256
                proposal.CheckpointSigningKeyId
                proposal.ValidUntil
            |> Some
            |> Task.FromResult
    }
