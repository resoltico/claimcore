module ClaimCore.IntegrationTests.BackupHealthReconciliationTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Expecto
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.BackupHealthSourceDocuments

let private privateNew path bytes =
    match PrivateFileService.writeNew 65536 path bytes with
    | Ok() -> ()
    | Error _ -> failtest "Synthetic publication output was not create-only private."

let private expected actual state message =
    match actual, state with
    | BackupHealthPublicationState.Complete, "COMPLETE"
    | BackupHealthPublicationState.CertificateOnly, "PARTIAL_CERTIFICATE"
    | BackupHealthPublicationState.SignatureOnly, "PARTIAL_SIGNATURE"
    | BackupHealthPublicationState.Missing, "MISSING"
    | BackupHealthPublicationState.Unknown, "UNKNOWN" -> ()
    | _ -> failtest message

let private response state token action digest =
    use document =
        DatabaseBackupHealthReconciliation.render state digest |> JsonDocument.Parse

    let root = document.RootElement

    Expect.equal
        (root.GetProperty("publicationState").GetString())
        token
        "Owner response reports the exact observed publication state."

    Expect.equal
        (root.GetProperty("recommendedAction").GetString())
        action
        "Owner response gives a safe state-specific next action."

    Expect.isFalse
        (root.GetProperty("realDataReady").GetBoolean())
        "Historical output readback never grants current health."

let private readbackResponses () =
    let digest = Some(String('a', 64))

    response BackupHealthPublicationState.Complete "COMPLETE" "REVERIFY_CURRENT_HEALTH" digest

    response
        BackupHealthPublicationState.CertificateOnly
        "PARTIAL_CERTIFICATE"
        "PRESERVE_PARTIAL_AND_ISSUE_FRESH_AT_NEW_PATH"
        digest

    response BackupHealthPublicationState.Missing "MISSING" "ISSUE_FRESH_AT_NEW_PATH" digest
    response BackupHealthPublicationState.Unknown "UNKNOWN" "QUARANTINE_AND_INSPECT" None

let private publicationStates () =
    let fixture = create ()

    try
        let certificate = fixture.Loaded.CertificateBytes
        let signature = fixture.Loaded.CertificateSignature

        let path name =
            Path.Combine(fixture.Root, name + ".json")

        let inspect = DatabaseBackupHealthReconciliation.inspect

        expected
            (inspect (path "absent") certificate signature)
            "MISSING"
            "Missing certificate and signature must stay missing."

        privateNew (path "certificate-only") certificate

        expected
            (inspect (path "certificate-only") certificate signature)
            "PARTIAL_CERTIFICATE"
            "One certificate file cannot imply signature publication."

        privateNew (path "signature-only" + ".sig") signature

        expected
            (inspect (path "signature-only") certificate signature)
            "PARTIAL_SIGNATURE"
            "A detached signature without certificate is an anomaly."

        privateNew (path "complete") certificate
        privateNew (path "complete" + ".sig") signature

        expected
            (inspect (path "complete") certificate signature)
            "COMPLETE"
            "Only exact original signed bytes count as complete publication."

        let changed = Array.copy certificate
        changed[0] <- changed[0] ^^^ 1uy
        privateNew (path "changed") changed
        privateNew (path "changed" + ".sig") signature

        expected
            (inspect (path "changed") certificate signature)
            "UNKNOWN"
            "Changed output bytes must never be overwritten or marked complete."

        File.CreateSymbolicLink(path "symlink", path "absent") |> ignore

        expected
            (inspect (path "symlink") certificate signature)
            "UNKNOWN"
            "A dangling output symlink is unsafe, not a missing publication."
    finally
        dispose fixture

let private historicalIssuer (fixture: SyntheticHealthDocuments) (claim: BackupHealthClaims) =
    let issuerKey = fixture.IssuerKey.PublicKey.Export(KeyBlobFormat.RawPublicKey)

    DatabaseBackupHealthHistorical.verifyIssuer
        issuerKey
        claim.SignerHolderActorId
        claim
        fixture.Loaded

    let changedIssuer = Array.copy fixture.Loaded.CertificateSignature
    changedIssuer[0] <- changedIssuer[0] ^^^ 1uy

    Expect.throws
        (fun () ->
            DatabaseBackupHealthHistorical.verifyIssuer
                issuerKey
                claim.SignerHolderActorId
                claim
                { fixture.Loaded with
                    CertificateSignature = changedIssuer
                })
        "Changed historical CHECKPOINT signature refuses readback."

let private historicalDocuments () =
    let fixture = create ()

    try
        let profile: ReviewedDeploymentProfile =
            {
                PublicationRootKey = Array.create 32 7uy
                BackupHealthPolicySha256 =
                    SHA256.HashData(fixture.Loaded.PolicyBytes) |> Convert.ToHexStringLower
            }

        let later = fixture.Claims.ValidUntil.AddMinutes(10.)

        Expect.isNone
            (BackupHealthCertificate.parse fixture.Loaded.CertificateBytes later)
            "Expired certificate cannot grant current backup health."

        let _, source, claim =
            DatabaseBackupHealthHistorical.verifiedDocuments profile fixture.Loaded later

        Expect.equal
            source.InstallationId
            claim.InstallationId
            "Historical signed source and certificate retain exact installation identity."

        historicalIssuer fixture claim

        let changed = Array.copy fixture.Loaded.ArchiveSignature
        changed[0] <- changed[0] ^^^ 1uy

        Expect.throws
            (fun () ->
                DatabaseBackupHealthHistorical.verifiedDocuments
                    profile
                    { fixture.Loaded with
                        ArchiveSignature = changed
                    }
                    later
                |> ignore)
            "Changed historical source signature refuses readback."
    finally
        dispose fixture

let private genericRootRefuses () =
    Expect.isNone
        (ReviewedDeploymentRoot.current ())
        "Generic checkout has no reviewed health root."

    use output = new MemoryStream()
    use errors = new MemoryStream()

    let code =
        DatabaseBackupHealthReconciliation.run "" "missing" "missing" "missing" output errors

    Expect.notEqual code 0 "Root absence cannot authenticate an output."
    Expect.equal output.Length 0L "No ready/complete response reaches stdout."
    use document = JsonDocument.Parse(errors.ToArray())

    Expect.equal
        (document.RootElement.GetProperty("publicationState").GetString())
        "UNKNOWN"
        "Root-closed readback reports only unknown."

    Expect.isFalse
        (document.RootElement.GetProperty("realDataReady").GetBoolean())
        "Historical readback never grants current real-data readiness."

let tests =
    testList
        "backup health publication readback"
        [
            testCase
                "[CC-BACKUP-001] historical health readback distinguishes complete partial missing and changed output"
                publicationStates
            testCase
                "[CC-BACKUP-001] expired historical health remains signed but cannot grant freshness"
                historicalDocuments
            testCase
                "[CC-BACKUP-001] historical health readback reports safe state-specific actions"
                readbackResponses
            testCase
                "[CC-BACKUP-001] generic build refuses historical health readback without input access"
                genericRootRefuses
        ]
