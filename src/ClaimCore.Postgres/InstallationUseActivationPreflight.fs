namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Witness

/// Identity, clock and current-health checks before one-way installation release.
module internal InstallationUseActivationPreflight =
    let state scope phase priorId priorSequence priorHash =
        {
            Scope = scope
            Phase = phase
            ActivationEventId = priorId
            ActivationSequence = priorSequence
            ActivationHash = priorHash
        }

    let databaseNow connection transaction =
        use command = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction)

        match command.ExecuteScalar() with
        | :? DateTimeOffset as value -> value.ToUniversalTime()
        | :? DateTime as value -> DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
        | _ -> invalidOp "Data-use activation database clock is unavailable."

    let reviewedPlan (profile: ReviewedDeploymentProfile) (plan: BackupHealthActivationPlan) =
        profile.BackupHealthPolicySha256 = plan.PolicySha256
        && plan.PublicationRootSha256 =
            (SHA256.HashData(profile.PublicationRootKey) |> Convert.ToHexStringLower)

    let proofShape
        (profile: ReviewedDeploymentProfile)
        (proof: BackupHealthQualifiedEvidence)
        (plan: BackupHealthActivationPlan)
        =
        proof.InstallationId <> Guid.Empty
        && proof.LineageId <> Guid.Empty
        && proof.Epoch > 0L
        && proof.WriterGeneration > 0L
        && proof.Canonical.Length > 0
        && proof.Canonical.Length <= 65536
        && proof.Signature.Length = 64
        && proof.WitnessTipHash.Length = 32
        && proof.KnownCopyInventorySha256.Length = 32
        && proof.CertificateSha256 = (SHA256.HashData(proof.Canonical) |> Convert.ToHexStringLower)
        && plan.Canonical.Length > 0
        && plan.Canonical.Length <= 16384
        && plan.PlanSha256 = (SHA256.HashData(plan.Canonical) |> Convert.ToHexStringLower)
        && plan.InstallationId = proof.InstallationId
        && plan.LineageId = proof.LineageId
        && plan.Epoch = proof.Epoch
        && plan.WriterGeneration = proof.WriterGeneration
        && plan.PolicySha256 = proof.PolicySha256
        && reviewedPlan profile plan

    let phaseMatches
        (proof: BackupHealthQualifiedEvidence)
        (witness: WitnessProtocol)
        (plan: BackupHealthActivationPlan)
        (state: InstallationUseState)
        generation
        identity
        snapshot
        now
        =
        identity = (proof.InstallationId, proof.LineageId, proof.Epoch)
        && witness.Identity.InstallationId = proof.InstallationId
        && witness.Identity.LineageId = proof.LineageId
        && witness.Identity.Epoch = proof.Epoch
        && generation = proof.WriterGeneration
        && state.Scope = InstallationUseScope.RealData
        && state.Phase = InstallationUsePhase.BootstrapNoCases
        && plan.PlanSha256.Length = 64
        && proof.ValidUntil > now
        && proof.CheckedAtDatabase <= now
        && snapshot.Use.Scope = InstallationUseScope.RealData
        && snapshot.Use.Phase = InstallationUsePhase.BootstrapNoCases
        && snapshot.TipSequence = proof.WitnessTipSequence
        && snapshot.TipHash = proof.WitnessTipHash

    let healthUnderLock primary transaction witness profile (proof: BackupHealthQualifiedEvidence) =
        BackupHealthRuntimeAdmission.verifyLocked
            primary
            transaction
            witness
            profile
            proof.PolicyCanonical
            proof.Canonical
            proof.Signature
