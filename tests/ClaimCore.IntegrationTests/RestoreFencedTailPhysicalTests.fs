module ClaimCore.IntegrationTests.RestoreFencedTailPhysicalTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Expecto
open Npgsql
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RestoreFencedTailSourceDocuments
open ClaimCore.IntegrationTests.RestoreWriterHandoffContext
open ClaimCore.IntegrationTests.RestoreWriterHandoffPhysicalTests

let private reportFiles (context: SettledW1Context) : RestoreReportFiles =
    {
        Report = context.Report.Evidence.Report
        Signature = context.Report.ReportSignature
        EvidenceIndex = context.Report.Evidence.EvidenceIndex
        ReportSha256 = context.Report.Evidence.ReportSha256
        EvidenceIndexSha256 = context.Report.Evidence.EvidenceIndexSha256
    }

let private signedReport (context: SettledW1Context) now =
    let report =
        DatabaseRestoreReportClaims.parse context.Report.Evidence.Report now
        |> Option.defaultWith (fun () -> failtest "Post-W1 signed report is invalid")

    let index =
        DatabaseRestoreEvidenceIndex.parse context.Report.Evidence.EvidenceIndex report
        |> Option.defaultWith (fun () -> failtest "Post-W1 signed index is invalid")

    report, index

let private probeSha (fence: byte array) =
    use document = JsonDocument.Parse(fence)

    document.RootElement.GetProperty("independentProbeSha256").GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "Synthetic fence probe digest is absent")

let private currentCustody (context: SettledW1Context) =
    let id = context.Witness.KeyCustody.ActiveKeyId
    let material = witnessKey ()
    let custody = new KeyRing(id, [ id, material ]) :> IKeyCustody
    CryptographicOperations.ZeroMemory(material)
    custody

let private inspect
    (context: SettledW1Context)
    (report: RestoreReportClaims)
    (index: RestoreEvidenceIndex)
    expectedProbe
    (evidence: SignedFencedTailEvidence)
    =
    use owner = new NpgsqlConnection(context.Access.Owner)
    owner.Open()
    use transaction = owner.BeginTransaction()

    let probeEvidenceSha =
        SHA256.HashData(Text.Encoding.ASCII.GetBytes("synthetic-independent-probe-evidence"))
        |> Convert.ToHexStringLower

    let verified =
        DatabaseRestoreFencedTailVerification.verify
            owner
            transaction
            context.Witness
            report
            index
            expectedProbe
            probeEvidenceSha
            context.Capture.ArchiveRoot
            evidence
            DateTimeOffset.UtcNow

    transaction.Rollback()
    verified

let private verifyHistoricalReport (context: SettledW1Context) now =
    use custody = currentCustody context
    use suppression = SuppressionKeyFile.Load(suppressionKeyFile ())

    let historical, settlement, ticket =
        DatabaseRestoreHandoffRecheck.afterSettlement
            context.Input.Publication
            "synthetic-only"
            context.Access.Owner
            context.Access.WitnessAudit
            context.Access.WitnessOwner
            custody
            suppression
            (reportFiles context)
            context.HandoffId
            context.Input.Publication.VerifierBinarySha256
            now

    Expect.equal
        historical.ReportSha256
        context.Report.Evidence.ReportSha256
        "Historical report digest"

    Expect.equal settlement.HandoffId context.HandoffId "Exact W1 settlement identity"
    Expect.equal ticket.Sequence context.W1Sequence "Exact post-W1 witness sequence"
    Expect.equal ticket.EntryHash context.W1Hash "Exact post-W1 witness hash"

let private verifyEvidence (context: SettledW1Context) report index supplement signature =

    let evidence: SignedFencedTailEvidence =
        {
            Report = context.Report.Evidence.Report
            ReportSignature = context.Report.ReportSignature
            Fence = context.Fence
            FenceSignature = context.FenceSignature
            Supplement = supplement
            SupplementSignature = signature
        }

    let expectedProbe = probeSha context.Fence
    let proof = inspect context report index expectedProbe evidence
    Expect.isFalse proof.RealDataReady "Same-Mac W1 tail cannot authorize real data"
    Expect.equal proof.W1Sequence context.W1Sequence "Exact W1 tail binds settlement"
    Expect.equal proof.Scope "synthetic-only" "Physical tail remains synthetic"

    Expect.isFalse
        (proof.ProbeEvidenceSha256 = proof.IndependentProbeSha256)
        "Probe publication digest is distinct from fence probe commitment"

    let historical: LoadedFencedTail =
        {
            ReportFiles = reportFiles context
            Evidence = evidence
        }

    let reconstructed =
        DatabaseFencedTailHistorical.verified
            context.Access.Owner
            context.Witness
            context.Input.Publication
            historical
            proof.ProbeEvidenceSha256
            context.Capture.ArchiveRoot

    Expect.equal
        reconstructed.FinalWalObjectSha256
        proof.FinalWalObjectSha256
        "Historical signer registration reconstructs the exact signed WAL candidate"

    proof, evidence, expectedProbe

let private refuseChangedEvidence
    (context: SettledW1Context)
    report
    index
    expectedProbe
    (evidence: SignedFencedTailEvidence)
    signature
    (objects: FencedWalObject list)
    =

    let changedSignature = Array.copy signature
    changedSignature[0] <- changedSignature[0] ^^^ 1uy

    Expect.throws
        (fun () ->
            inspect
                context
                report
                index
                expectedProbe
                { evidence with
                    SupplementSignature = changedSignature
                }
            |> ignore)
        "Changed detached tail signature refuses"

    let first = objects.Head
    let source = Path.Combine(context.Capture.ArchiveRoot, first.RelativePath)
    let withheld = source + ".withheld"
    File.Move(source, withheld)

    try
        Expect.throws
            (fun () -> inspect context report index expectedProbe evidence |> ignore)
            "Missing encrypted final WAL object refuses"
    finally
        File.Move(withheld, source)

let private verifiedTail (context: SettledW1Context) =
    let primaryFinal, witnessFinal, objects = capture context

    let supplement, signature =
        signedSupplement context primaryFinal witnessFinal objects

    let now = DateTimeOffset.UtcNow
    let report, index = signedReport context now
    verifyHistoricalReport context now

    let proof, evidence, expectedProbe =
        verifyEvidence context report index supplement signature

    refuseChangedEvidence context report index expectedProbe evidence signature objects

    proof

let internal withVerifiedFencedTail action owner app writer witness =
    withSettledPhysicalPair
        (fun context ->
            let proof = verifiedTail context
            action context proof)
        owner
        app
        writer
        witness

let tests =
    testList
        "physical fenced recovery tail"
        [
            testCase
                "[CC-BACKUP-001] signed post-W1 final WAL tail rechecks without readiness"
                (fun _ -> withAuthorityRuntimeDatabase (withVerifiedFencedTail (fun _ _ -> ())))
        ]
