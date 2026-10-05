module internal ClaimCore.IntegrationTests.RestoreWriterHandoffQualificationFixture

open System
open Expecto
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.RestorePhysicalConnections

let requireClosedOwnerCommand outcome =
    match outcome with
    | Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed) -> ()
    | _ -> failtest "Production owner W1 requires a reviewed publication root"

let verifyProspectiveFence
    (access: RestoredPairAccess)
    (input: RestoreProduceInput)
    (produced: SignedRestoreProduction)
    (custody: IKeyCustody)
    (suppression: SuppressionKeyFile)
    prepare
    fence
    signedFence
    =
    let reportEvidence: RestoreReportFiles =
        {
            Report = produced.Evidence.Report
            Signature = produced.ReportSignature
            EvidenceIndex = produced.Evidence.EvidenceIndex
            ReportSha256 = produced.Evidence.ReportSha256
            EvidenceIndexSha256 = produced.Evidence.EvidenceIndexSha256
        }

    let candidate =
        ClaimCore.Postgres.WriterHandoffPreparation.parse prepare
        |> Option.defaultWith (fun () -> failtest "W1 PREPARE candidate is invalid")

    DatabaseWriterHandoffQualification.preparation
        input.Publication
        "synthetic-only"
        access.Owner
        access.WitnessAudit
        access.WitnessOwner
        custody
        suppression
        reportEvidence
        candidate
        prepare
        fence
        signedFence
        input.Publication.VerifierBinarySha256
        DateTimeOffset.UtcNow
    |> fun work -> work.GetAwaiter().GetResult()
