module internal ClaimCore.IntegrationTests.RestoreProduceSignedPairChecks

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Expecto
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RestorePhysicalConnections
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestoreProduceCanonicalTests
open ClaimCore.IntegrationTests.RestoreProduceInventoryDigestChecks

let auditBorrowed (access: RestoredPairAccess) (witness: WitnessProtocol) =
    let keyId = witness.KeyCustody.ActiveKeyId
    let material = witnessKey ()
    let borrowed = new KeyRing(keyId, [ keyId, material ]) :> IKeyCustody
    CryptographicOperations.ZeroMemory(material)
    use suppression = SuppressionKeyFile.Load(suppressionKeyFile ())

    try
        let audit () =
            let _, tip, _ =
                DatabaseVerifyData.auditedRestoredWith
                    access.Owner
                    access.WitnessAudit
                    borrowed
                    suppression
                    (fun _ _ _ _ tip -> System.Threading.Tasks.Task.FromResult tip.TipSequence)
                |> await

            tip.TipSequence, tip.TipHash

        let firstAudit = audit ()
        let secondAudit = audit ()

        Expect.equal
            secondAudit
            firstAudit
            "Two exact independent restored audits reuse caller custody"

        Expect.isTrue (borrowed.HasKey keyId) "Audits leave the caller-owned witness key live"
    finally
        borrowed.Dispose()

    Expect.isFalse (borrowed.HasKey keyId) "Only the caller disposes its witness key ring"

let reportFiles (produced: SignedRestoreProduction) : RestoreReportFiles =
    {
        Report = produced.Evidence.Report
        Signature = produced.ReportSignature
        EvidenceIndex = produced.Evidence.EvidenceIndex
        ReportSha256 = produced.Evidence.ReportSha256
        EvidenceIndexSha256 = produced.Evidence.EvidenceIndexSha256
    }

let reportScope (produced: SignedRestoreProduction) =
    use report = JsonDocument.Parse(produced.Evidence.Report)
    let claims = report.RootElement
    Expect.equal (claims.GetProperty("scope").GetString()) "synthetic-only" "Report scope is exact"

    Expect.isFalse
        (claims.GetProperty("realDataReady").GetBoolean())
        "Synthetic report cannot admit real data"

    Expect.isTrue
        (claims.GetProperty("recoveryTailUnsealed").GetBoolean())
        "Later W1 tail remains open"

    Expect.isNone (DatabaseRestorePublication.current ()) "No production publication root exists"

let verifyCoverage (input: RestoreProduceInput) (facts: RestoredPairFacts) =
    let template, _, _ = specimen ()

    DatabaseRestoreWalCoverage.verify
        input.Index
        { template with
            PrimaryTimeline = facts.PrimaryTimeline
            WitnessTimeline = facts.WitnessTimeline
        }


let verifyInventory input produced facts checkpointKey =
    verifyInventoryDigests input produced facts checkpointKey

let private inspect
    (access: RestoredPairAccess)
    (custody: IKeyCustody)
    (suppression: SuppressionKeyFile)
    (input: RestoreProduceInput)
    (files: RestoreReportFiles)
    nonce
    =
    DatabaseRestoreReportRecheck.evaluateSynthetic
        (Some input.Publication)
        access.Owner
        access.WitnessAudit
        access.WitnessOwner
        custody
        suppression
        files
        nonce
        input.Publication.VerifierBinarySha256
        DateTimeOffset.UtcNow
    |> await

let private changedSignature
    (access: RestoredPairAccess)
    custody
    suppression
    input
    (files: RestoreReportFiles)
    nonce
    =
    let changed = Array.copy files.Signature
    changed[0] <- changed[0] ^^^ 1uy

    match inspect access custody suppression input { files with Signature = changed } nonce with
    | Error RestoreRecheckFailure.EvidenceMismatch -> ()
    | _ -> failtest "Changed detached report signature must refuse"

let private missingWal
    (access: RestoredPairAccess)
    custody
    suppression
    input
    (files: RestoreReportFiles)
    nonce
    (registered: RegisteredWalCapture)
    =
    let source = registered.Objects.Head.CiphertextPath
    let displaced = source + ".withheld"
    File.Move(source, displaced)

    try
        match inspect access custody suppression input files nonce with
        | Error RestoreRecheckFailure.EvidenceMismatch -> ()
        | _ -> failtest "Missing registered WAL object must refuse"
    finally
        File.Move(displaced, source)

let verifyRecheck
    (access: RestoredPairAccess)
    (custody: IKeyCustody)
    (suppression: SuppressionKeyFile)
    (input: RestoreProduceInput)
    (registered: RegisteredWalCapture)
    (produced: SignedRestoreProduction)
    =
    let files = reportFiles produced
    let nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))

    Expect.isOk
        (inspect access custody suppression input files nonce)
        "Exact signed report and physical pair recheck"

    match
        DatabaseRestoreReportRecheck.evaluate
            None
            access.Owner
            access.WitnessAudit
            access.WitnessOwner
            custody
            suppression
            files
            nonce
            input.Publication.VerifierBinarySha256
            DateTimeOffset.UtcNow
        |> await
    with
    | Error RestoreRecheckFailure.TrustAnchorUnavailable -> ()
    | _ -> failtest "Missing publication root must refuse owner admission"

    changedSignature access custody suppression input files nonce
    missingWal access custody suppression input files nonce registered
