module ClaimCore.WebTests.RealDataActivationWireTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts

let private id (suffix: int) =
    Guid.Parse("10000000-0000-4000-8000-" + suffix.ToString("D12"))

let private hash (text: string) =
    SHA256.HashData(Encoding.ASCII.GetBytes text)

let private sampleFacts () =
    let canonical = Encoding.ASCII.GetBytes("{\"format\":\"synthetic-wire-sample\"}\n")

    {
        PlanId = id 1
        ActivationId = id 2
        InstallationId = id 3
        LineageId = id 4
        Epoch = 1L
        WriterGeneration = 2L
        PolicySha256 = hash "policy"
        PublicationRootSha256 = hash "root"
        CycleId = id 5
        LeaseId = id 6
        CaptureReceiptSha256 = hash "capture"
        PrimaryBaseCopyId = id 7
        WitnessBaseCopyId = id 8
        PrimaryBasePhysicalReceiptSha256 = hash "primary"
        WitnessBasePhysicalReceiptSha256 = hash "witness"
        CheckpointObjectSha256 = hash "checkpoint"
        TestRestoreReportSha256 = hash "restore"
        TestRestoreFullAuditSha256 = hash "audit"
        MinimumArtifactCutoffSequence = 41L
        MinimumPrimaryWalHorizon = "0/1000000"
        MinimumWitnessWalHorizon = "0/2000000"
        CanonicalPlan = canonical
        PlanSha256 = SHA256.HashData(canonical)
        PublishedAt = DateTimeOffset.Parse("2026-09-27T10:00:00+00:00")
        PublicationWitnessSequence = 43L
        PublicationWitnessHash = hash "publication"
        ApprovalExpiresNoLaterThan = DateTimeOffset.Parse("2026-09-28T10:00:00+00:00")
    }

let private review () =
    let facts = sampleFacts ()

    use document =
        JsonDocument.Parse(
            WebWireCodec.realDataActivationReview (
                RealDataActivationPlanReviewOutcome.Reviewed facts
            )
        )

    let root = document.RootElement

    Expect.equal
        (root.GetProperty("endpoint").GetString())
        "authority.reviewRealDataActivation"
        "Review endpoint is exact."

    let outcome = root.GetProperty("outcome")
    Expect.equal (outcome.GetProperty("tag").GetString()) "REVIEWED" "Review is typed."
    let data = outcome.GetProperty("data")

    Expect.equal
        (data.GetProperty("canonicalPlan").GetString())
        (Encoding.ASCII.GetString facts.CanonicalPlan)
        "Exact plan text is disclosed."

    Expect.equal
        (data.GetProperty("planSha256").GetString())
        (Convert.ToHexStringLower facts.PlanSha256)
        "Plan digest is exact."

    Expect.equal
        (data.GetProperty("primaryBaseCopyId").GetGuid())
        facts.PrimaryBaseCopyId
        "BASE identity is typed."

    Expect.isFalse
        (data.TryGetProperty("approverActorId") |> fst)
        "Review does not invent an actor identity."

let tests =
    testList
        "real-data activation wire"
        [
            testCase
                "[CC-WEB-001] owner plan review projects exact canonical bytes and typed nonclaimant facts"
                review
        ]
