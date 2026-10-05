namespace ClaimCore.Database

open System
open System.IO
open System.Security.Cryptography
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres

/// The independent custodians and current CHECKPOINT holder sign exact bytes before this
/// owner process rechecks live facts and publishes the detached signature last.
module internal DatabaseBackupHealthExecution =
    let private databaseNow (owner: NpgsqlConnection) =
        use command = new NpgsqlCommand("SELECT clock_timestamp()", owner)

        match command.ExecuteScalar() with
        | :? DateTimeOffset as value -> value.ToUniversalTime()
        | :? DateTime as value -> DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
        | _ -> invalidOp "Backup health database clock is unavailable."

    let private privateOutput (output: string) =
        if
            not (Path.IsPathFullyQualified(output))
            || not (output.EndsWith(".json", StringComparison.Ordinal))
        then
            invalidOp "Backup health output path is invalid."

        let directory =
            Path.GetDirectoryName(output)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Backup health output directory is absent.")

        match PrivateFileService.requirePrivateDirectory directory with
        | Ok() -> output, output + ".sig"
        | Error _ -> invalidOp "Backup health output directory is unsafe."

    let private matching maximum path (expected: byte array) =
        match PrivateFileService.readBinary maximum path with
        | Ok bytes ->
            try
                CryptographicOperations.FixedTimeEquals(bytes.AsSpan(), expected.AsSpan())
            finally
                CryptographicOperations.ZeroMemory(bytes.AsSpan())
        | Error _ -> false

    let internal publish output (proof: BackupHealthQualifiedEvidence) =
        let canonical, signature = privateOutput output

        let write maximum path bytes =
            match PrivateFileService.writeNew maximum path bytes with
            | Ok() -> ()
            | Error _ -> invalidOp "Backup health output publication was refused."

        let canonicalExists = File.Exists canonical
        let signatureExists = File.Exists signature

        if signatureExists && not canonicalExists then
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed
        elif canonicalExists && not (matching 65536 canonical proof.Canonical) then
            AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
        elif signatureExists && not (matching 64 signature proof.Signature) then
            AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
        else
            try
                if not canonicalExists then
                    write 65536 canonical proof.Canonical

                if not signatureExists then
                    write 64 signature proof.Signature

                AdministrationOutcome.Completed None
            with _ ->
                // Exact partial bytes remain for a create-only retry, never deletion/overwrite.
                AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed

    let private qualified ownerConnection (profile: ReviewedDeploymentProfile) loaded =
        task {
            let policy =
                BackupHealthPolicyCodec.parse loaded.PolicyBytes profile.BackupHealthPolicySha256
                |> Option.defaultWith (fun () ->
                    invalidOp "Reviewed backup health policy is invalid.")

            let builder = OwnerConnection.builder ownerConnection
            use owner = new NpgsqlConnection(builder.ConnectionString)
            owner.Open()
            let now = databaseNow owner
            let evidence = DatabaseBackupHealthIndependentProof.verify policy loaded now
            do! DatabaseBackupHealthCaptureLink.verify ownerConnection policy evidence

            let! claim, verifiedAt =
                DatabaseBackupHealthLive.verify ownerConnection policy evidence loaded

            let proof =
                {
                    InstallationId = claim.InstallationId
                    LineageId = claim.LineageId
                    Epoch = claim.Epoch
                    WriterGeneration = claim.WriterGeneration
                    PolicySha256 = profile.BackupHealthPolicySha256
                    PolicyCanonical = Array.copy loaded.PolicyBytes
                    CertificateSha256 =
                        SHA256.HashData(loaded.CertificateBytes) |> Convert.ToHexStringLower
                    WitnessTipSequence = claim.WitnessTipSequence
                    WitnessTipHash = Convert.FromHexString(claim.WitnessTipHash)
                    KnownCopyInventorySha256 = Convert.FromHexString(claim.KnownCopyInventorySha256)
                    CheckedAtDatabase = verifiedAt
                    ValidUntil = claim.ValidUntil
                    SignerKeyId = claim.SignerKeyId
                    SignerHolderActorId = claim.SignerHolderActorId
                    Canonical = Array.copy loaded.CertificateBytes
                    Signature = Array.copy loaded.CertificateSignature
                }

            return proof, evidence
        }

    let qualify ownerConnection policyPath evidencePath =
        task {
            match ReviewedDeploymentRoot.current () with
            | None -> return Ok None
            | Some profile ->
                let loaded =
                    try
                        Some(DatabaseBackupHealthEvidenceInputs.load policyPath evidencePath)
                    with _ ->
                        None

                match loaded with
                | None ->
                    CryptographicOperations.ZeroMemory(profile.PublicationRootKey)
                    return Error DatabaseInputProblem.BackupHealthFileRefused
                | Some loaded ->
                    try
                        try
                            let! proof, _ = qualified ownerConnection profile loaded
                            return Ok(Some proof)
                        with _ ->
                            return Ok None
                    finally
                        DatabaseBackupHealthEvidenceInputs.dispose loaded
                        CryptographicOperations.ZeroMemory(profile.PublicationRootKey)
        }

    let activationPlan ownerConnection policyPath evidencePath =
        task {
            match ReviewedDeploymentRoot.current () with
            | None -> return Ok None
            | Some profile ->
                let loaded =
                    try
                        Some(DatabaseBackupHealthEvidenceInputs.load policyPath evidencePath)
                    with _ ->
                        None

                match loaded with
                | None ->
                    CryptographicOperations.ZeroMemory(profile.PublicationRootKey)
                    return Error DatabaseInputProblem.BackupHealthFileRefused
                | Some loaded ->
                    try
                        try
                            let! _, source = qualified ownerConnection profile loaded

                            return
                                DatabaseBackupHealthActivationPlan.create profile source
                                |> Some
                                |> Ok
                        with _ ->
                            return Ok None
                    finally
                        DatabaseBackupHealthEvidenceInputs.dispose loaded
                        CryptographicOperations.ZeroMemory(profile.PublicationRootKey)
        }

    let private activationEvidence
        ownerConnection
        (profile: ReviewedDeploymentProfile)
        (original: LoadedBackupHealthEvidence)
        (fresh: LoadedBackupHealthEvidence)
        expectedPlanSha
        =
        task {
            let policy =
                BackupHealthPolicyCodec.parse original.PolicyBytes profile.BackupHealthPolicySha256
                |> Option.defaultWith (fun () -> invalidOp "Reviewed health policy differs.")

            if
                not (
                    CryptographicOperations.FixedTimeEquals(
                        original.PolicyBytes.AsSpan(),
                        fresh.PolicyBytes.AsSpan()
                    )
                )
            then
                invalidOp "Activation health policy changed."

            let builder = OwnerConnection.builder ownerConnection
            use owner = new NpgsqlConnection(builder.ConnectionString)
            owner.Open()
            let now = databaseNow owner

            let source =
                DatabaseBackupHealthIndependentProof.verifyHistorical policy original now

            do! DatabaseBackupHealthCaptureLink.verify ownerConnection policy source
            let plan = DatabaseBackupHealthActivationPlan.create profile source

            if plan.PlanSha256 <> expectedPlanSha then
                invalidOp "Activation plan differs from approved bytes."

            let! qualified, currentSource = qualified ownerConnection profile fresh

            if not (DatabaseBackupHealthActivationPlan.meets plan currentSource) then
                invalidOp "Current health does not satisfy approved physical minimum."

            return qualified, plan
        }

    let qualifyForActivation
        ownerConnection
        policyPath
        originalEvidencePath
        freshEvidencePath
        expectedPlanSha
        =
        task {
            match ReviewedDeploymentRoot.current () with
            | None -> return Ok None
            | Some profile ->
                let loaded =
                    try
                        let original =
                            DatabaseBackupHealthEvidenceInputs.load policyPath originalEvidencePath

                        try
                            let fresh =
                                DatabaseBackupHealthEvidenceInputs.load policyPath freshEvidencePath

                            Some(original, fresh)
                        with _ ->
                            DatabaseBackupHealthEvidenceInputs.dispose original
                            None
                    with _ ->
                        None

                match loaded with
                | None ->
                    CryptographicOperations.ZeroMemory(profile.PublicationRootKey)
                    return Error DatabaseInputProblem.BackupHealthFileRefused
                | Some(original, fresh) ->
                    try
                        try
                            let! proof =
                                activationEvidence
                                    ownerConnection
                                    profile
                                    original
                                    fresh
                                    expectedPlanSha

                            return Ok(Some proof)
                        with _ ->
                            return Ok None
                    finally
                        DatabaseBackupHealthEvidenceInputs.dispose original
                        DatabaseBackupHealthEvidenceInputs.dispose fresh
                        CryptographicOperations.ZeroMemory(profile.PublicationRootKey)
        }

    let run ownerConnection policyPath evidencePath outputPath =
        match (qualify ownerConnection policyPath evidencePath).GetAwaiter().GetResult() with
        | Error reason -> Error reason
        | Ok None -> Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed)
        | Ok(Some proof) ->
            try
                Ok(publish outputPath proof)
            with _ ->
                Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed)
