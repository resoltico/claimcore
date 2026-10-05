module ClaimCore.IntegrationTests.BackupHealthSourceTests

open System
open System.IO
open System.Security.Cryptography
open Expecto
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.BackupHealthSourceDocuments

let private assertRefusals fixture (evidence: BackupHealthEvidence) now =
    let changed = Array.copy fixture.Loaded.ArchiveSignature
    changed[0] <- changed[0] ^^^ 1uy

    Expect.throws
        (fun () ->
            DatabaseBackupHealthIndependentProof.verify
                fixture.Policy
                { fixture.Loaded with
                    ArchiveSignature = changed
                }
                now
            |> ignore)
        "A changed archive-host signature cannot issue health"

    let wrongPolicy =
        BackupHealthPolicyCodec.parse fixture.Loaded.PolicyBytes (String('0', 64))

    Expect.isNone wrongPolicy "An owner-selected policy cannot replace build-pinned bytes"

    let first = evidence.Objects.Head
    let source = Path.Combine(fixture.Policy.ArchiveRoot, first.RelativePath)
    let withheld = source + ".withheld"
    File.Move(source, withheld)

    try
        Expect.throws
            (fun () ->
                DatabaseBackupHealthIndependentProof.verify fixture.Policy fixture.Loaded now
                |> ignore)
            "A missing retained ciphertext refuses health issuance"
    finally
        File.Move(withheld, source)

let private signedIndependentSource =
    testCase
        "[CC-BACKUP-001] three independently signed current health source roles bind physical WAL and restore facts"
        (fun _ ->
            let fixture = create ()

            try
                let now = DateTimeOffset.UtcNow

                let evidence =
                    DatabaseBackupHealthIndependentProof.verify fixture.Policy fixture.Loaded now

                Expect.equal evidence.Objects.Length 4 "Two BASE and two WAL objects are present"

                DatabaseBackupHealthBinding.verify
                    fixture.Policy
                    evidence
                    fixture.Claims
                    fixture.Loaded.SourceBytes
                    now

                DatabaseBackupHealthWalCoverage.verify evidence

                assertRefusals fixture evidence now
            finally
                dispose fixture)

let private temporalCertificateBounds =
    testCase
        "[CC-BACKUP-001] health expiry is exclusive and historical readback grants no current validity"
        (fun _ ->
            let fixture = create ()

            try
                let value = fixture.Claims
                let canonical = fixture.Loaded.CertificateBytes

                let futureWal =
                    { value with
                        PrimaryWal =
                            { value.PrimaryWal with
                                VerifiedAt = value.CheckedAt.AddSeconds(1.)
                            }
                    }

                Expect.isNone
                    (BackupHealthCertificate.parse
                        (BackupHealthRuntimeActorRaceFixture.certificate futureWal)
                        value.CheckedAt)
                    "An observation sampled after certificate capture cannot be hidden by a fixture clock"

                Expect.isSome
                    (BackupHealthCertificate.parse canonical value.CheckedAt)
                    "Positive current certificate control"

                Expect.isNone
                    (BackupHealthCertificate.parse canonical (value.CheckedAt.AddTicks(-1L)))
                    "Future observation refuses without a clock-skew allowance"

                Expect.isSome
                    (BackupHealthCertificate.parse canonical (value.ValidUntil.AddTicks(-1L)))
                    "Last instant before expiry remains current"

                Expect.isNone
                    (BackupHealthCertificate.parse canonical value.ValidUntil)
                    "Exact expiry is not current authority"

                Expect.isSome
                    (BackupHealthCertificate.parseHistorical canonical value.ValidUntil)
                    "Historical readback preserves signed original evidence"

                Expect.isNone
                    (BackupHealthCertificate.parse canonical (value.ValidUntil.AddHours(1.)))
                    "Historical readback cannot renew fresh authority"
            finally
                dispose fixture)

let private rootClosed =
    testCase
        "[CC-BACKUP-001] generic source preview refuses full backup health before private input"
        (fun _ ->
            Expect.isNone
                (ReviewedDeploymentRoot.current ())
                "Generic checkout contains no real-data deployment root"

            match DatabaseBackupHealthExecution.run "" "absent" "absent" "absent" with
            | Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed) -> ()
            | _ -> failtest "Generic root absence must refuse without database or file access")

let private exactPublication =
    testCase
        "[CC-BACKUP-001] backup health publication reconciles exact partial and complete bytes"
        (fun _ ->
            let fixture = create ()

            try
                let claim = fixture.Claims
                let loaded = fixture.Loaded

                let proof: BackupHealthQualifiedEvidence =
                    {
                        InstallationId = claim.InstallationId
                        LineageId = claim.LineageId
                        Epoch = claim.Epoch
                        WriterGeneration = claim.WriterGeneration
                        PolicySha256 = String('a', 64)
                        PolicyCanonical = Array.copy loaded.PolicyBytes
                        CertificateSha256 =
                            SHA256.HashData(loaded.CertificateBytes) |> Convert.ToHexStringLower
                        WitnessTipSequence = claim.WitnessTipSequence
                        WitnessTipHash = Convert.FromHexString(claim.WitnessTipHash)
                        KnownCopyInventorySha256 =
                            Convert.FromHexString(claim.KnownCopyInventorySha256)
                        CheckedAtDatabase = claim.CheckedAt
                        ValidUntil = claim.ValidUntil
                        SignerKeyId = claim.SignerKeyId
                        SignerHolderActorId = claim.SignerHolderActorId
                        Canonical = loaded.CertificateBytes
                        Signature = loaded.CertificateSignature
                    }

                let path = Path.Combine(fixture.Root, "health.json")
                let partial = Path.Combine(fixture.Root, "partial.json")

                let completed outcome message =
                    match outcome with
                    | AdministrationOutcome.Completed None -> ()
                    | _ -> failtest message

                match PrivateFileService.writeNew 65536 partial proof.Canonical with
                | Ok() -> ()
                | Error _ -> failtest "Synthetic partial publication could not be created"

                completed
                    (DatabaseBackupHealthExecution.publish partial proof)
                    "Exact partial publication creates only its missing signature"

                completed
                    (DatabaseBackupHealthExecution.publish partial proof)
                    "Exact complete publication is an idempotent readback"

                completed
                    (DatabaseBackupHealthExecution.publish path proof)
                    "Fresh output creates canonical and detached signature"
            finally
                dispose fixture)

let private planChanges (fixture: SyntheticHealthDocuments) (plan: BackupHealthActivationPlan) =
    let advanced =
        { fixture.Evidence with
            AuthorityRevision = fixture.Evidence.AuthorityRevision + 2L
            WitnessTipSequence = fixture.Evidence.WitnessTipSequence + 4L
            KnownCopyInventorySha256 = String('f', 64)
            ArtifactCutoffSequence = fixture.Evidence.ArtifactCutoffSequence + 1L
        }

    Expect.isTrue
        (DatabaseBackupHealthActivationPlan.meets plan advanced)
        "Later authority, inventory and artifact cutoff do not invalidate the stable plan"

    let changedBase =
        { advanced with
            Objects =
                advanced.Objects
                |> List.map (fun item ->
                    if item.Cluster = "PRIMARY" && item.Kind = "BASE" then
                        { item with
                            PhysicalReceiptSha256 = String('f', 64)
                        }
                    else
                        item)
        }

    Expect.isFalse
        (DatabaseBackupHealthActivationPlan.meets plan changedBase)
        "A different BASE receipt cannot inherit approval"

    let weakerWal =
        { advanced with
            Objects =
                advanced.Objects
                |> List.map (fun item ->
                    if item.Cluster = "PRIMARY" && item.Kind = "WAL" then
                        { item with WalHorizon = "0/0" }
                    else
                        item)
        }

    Expect.isFalse
        (DatabaseBackupHealthActivationPlan.meets plan weakerWal)
        "A shorter WAL prefix cannot satisfy the approved minimum"

    Expect.isFalse
        (DatabaseBackupHealthActivationPlan.meets
            plan
            { advanced with
                ArtifactCutoffSequence = plan.MinimumArtifactCutoffSequence - 1L
            })
        "Artifact cutoff rollback cannot inherit approval"

let private historicalSource (fixture: SyntheticHealthDocuments) =
    let later = fixture.Evidence.CheckedAt.AddMinutes(10.)

    Expect.throws
        (fun () ->
            DatabaseBackupHealthIndependentProof.verify fixture.Policy fixture.Loaded later
            |> ignore)
        "Expired source cannot issue current health"

    DatabaseBackupHealthIndependentProof.verifyHistorical fixture.Policy fixture.Loaded later
    |> ignore

let private stableActivationPlan =
    testCase
        "[CC-BACKUP-001] activation plan holds physical minima while live authority advances"
        (fun _ ->
            let fixture = create ()

            try
                let profile: ReviewedDeploymentProfile =
                    {
                        PublicationRootKey = Array.create 32 7uy
                        BackupHealthPolicySha256 =
                            SHA256.HashData(fixture.Loaded.PolicyBytes) |> Convert.ToHexStringLower
                    }

                let plan = DatabaseBackupHealthActivationPlan.create profile fixture.Evidence

                Expect.isTrue
                    (DatabaseBackupHealthActivationPlan.meets plan fixture.Evidence)
                    "Original source satisfies its approved physical plan"

                planChanges fixture plan
                historicalSource fixture
            finally
                dispose fixture)

let tests =
    testList
        "backup health source"
        [
            signedIndependentSource
            temporalCertificateBounds
            rootClosed
            exactPublication
            stableActivationPlan
        ]
