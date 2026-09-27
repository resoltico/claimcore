namespace ClaimCore.Database

open System
open System.IO
open ClaimCore.HostSecurity
open ClaimCore.Postgres

/// Three distinct source-pinned custodians attest the same canonical source; the owner also
/// opens each named private object through a nofollow handle and recomputes its exact hash.
module internal DatabaseBackupHealthIndependentProof =
    let private role (policy: BackupHealthPolicy) name =
        policy.Roles |> List.find (fun item -> item.Role = name)

    let private signature key source signature =
        if not (ManagedCopySignature.verify key source signature) then
            invalidOp "Independent backup health signature is invalid."

    let private privateRoot path =
        match PrivateFileService.requirePrivateDirectory path with
        | Ok() -> ()
        | Error _ -> invalidOp "Independent backup health custody root is unsafe."

    let private objectHash root relative maximum expected =
        let path = Path.Combine(root, relative)

        match PrivateFileService.hashPrivateFile maximum path with
        | Ok(length, hash) when length > 0L && Convert.ToHexStringLower(hash) = expected -> length
        | _ -> invalidOp "Independent backup health object is missing or changed."

    let private objects (policy: BackupHealthPolicy) (evidence: BackupHealthEvidence) =
        privateRoot policy.ArchiveRoot
        privateRoot policy.CheckpointRoot
        privateRoot policy.RestoreRoot

        for item in evidence.Objects do
            let actual =
                objectHash
                    policy.ArchiveRoot
                    item.RelativePath
                    item.CiphertextBytes
                    item.CiphertextSha256

            if actual <> item.CiphertextBytes then
                invalidOp "Independent archive object length changed."

        let checkpoint = evidence.Checkpoint

        if checkpoint.RelativePath <> evidence.CycleId.ToString("D") + ".json" then
            invalidOp "Independent checkpoint is not the sealed cycle object."

        let actualCheckpoint =
            objectHash
                policy.CheckpointRoot
                checkpoint.RelativePath
                checkpoint.ObjectBytes
                checkpoint.ObjectSha256

        if actualCheckpoint <> checkpoint.ObjectBytes then
            invalidOp "Independent checkpoint object length changed."

        let restored = evidence.TestRestore

        objectHash policy.RestoreRoot restored.ReportRelativePath 131072L restored.ReportSha256
        |> ignore

        objectHash policy.RestoreRoot restored.AuditRelativePath 1048576L restored.FullAuditSha256
        |> ignore

    let private ages (policy: BackupHealthPolicy) (value: BackupHealthEvidence) =
        let within maximum observed =
            observed <= value.CheckedAt
            && value.CheckedAt - observed <= TimeSpan.FromSeconds(float maximum)

        let baseCopies = value.Objects |> List.filter (fun item -> item.Kind = "BASE")
        let walCopies = value.Objects |> List.filter (fun item -> item.Kind = "WAL")

        if
            not (
                baseCopies
                |> List.forall (fun item -> within policy.MaximumBackupAgeSeconds item.VerifiedAt)
            )
            || not (
                walCopies
                |> List.forall (fun item -> within policy.MaximumWalLagSeconds item.VerifiedAt)
            )
            || not (within policy.MaximumCheckpointAgeSeconds value.Checkpoint.VerifiedAt)
            || not (within policy.MaximumRestoreTestAgeSeconds value.TestRestore.VerifiedAt)
        then
            invalidOp "Independent backup health evidence exceeds the reviewed policy."

    let private verified
        (policy: BackupHealthPolicy)
        (loaded: LoadedBackupHealthEvidence)
        (parse: byte array -> DateTimeOffset -> BackupHealthEvidence option)
        (now: DateTimeOffset)
        =
        let source = loaded.SourceBytes
        signature (role policy "archive").PublicKey source loaded.ArchiveSignature
        signature (role policy "checkpoint").PublicKey source loaded.CheckpointSignature
        signature (role policy "test-restore").PublicKey source loaded.RestoreSignature

        let evidence =
            parse source now
            |> Option.defaultWith (fun () ->
                invalidOp "Independent backup health source is invalid or stale.")

        if evidence.PolicyId <> policy.PolicyId then
            invalidOp "Independent backup health policy differs."

        ages policy evidence
        objects policy evidence
        evidence

    let verify (policy: BackupHealthPolicy) loaded now =
        verified policy loaded DatabaseBackupHealthEvidenceClaims.parse now

    let verifyHistorical (policy: BackupHealthPolicy) loaded now =
        verified policy loaded DatabaseBackupHealthEvidenceClaims.parseHistorical now

    /// Readback of an already signed publication checks original issuance, not
    /// current archive availability; this result cannot issue a new certificate.
    let verifyHistoricalSignatures
        (policy: BackupHealthPolicy)
        (loaded: LoadedBackupHealthEvidence)
        now
        =
        let source = loaded.SourceBytes
        signature (role policy "archive").PublicKey source loaded.ArchiveSignature
        signature (role policy "checkpoint").PublicKey source loaded.CheckpointSignature
        signature (role policy "test-restore").PublicKey source loaded.RestoreSignature

        let evidence =
            DatabaseBackupHealthEvidenceClaims.parseHistorical source now
            |> Option.defaultWith (fun () ->
                invalidOp "Historical independent backup source is invalid.")

        if evidence.PolicyId <> policy.PolicyId then
            invalidOp "Historical independent backup policy differs."

        ages policy evidence
        evidence
