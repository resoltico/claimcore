module ClaimCore.WebTests.TombstoneTerminalInputTests

open System
open System.Text
open Expecto
open ClaimCore.Application
open ClaimCore.Web

let private bytes (value: string) = Encoding.UTF8.GetBytes value
let private eventId = "40000000-0000-4000-8000-000000000001"
let private caseId = "50000000-0000-4000-8000-000000000001"
let private approvalId = "60000000-0000-4000-8000-000000000001"
let private digest = String.replicate 64 "a"

let private copy =
    $"""{{
      "eventId":"{eventId}",
      "caseId":"{caseId}",
      "expectedAuthorityRevision":"2",
      "expectedAuthorityHash":"{digest}",
      "installationId":"70000000-0000-4000-8000-000000000001",
      "lineageId":"80000000-0000-4000-8000-000000000001",
      "witnessEpoch":"1",
      "pruneEventId":"{approvalId}",
      "witnessCutoffSequence":"12",
      "witnessCutoffHash":"{digest}",
      "copyInventoryDigest":"{digest}",
      "relevantCopyCount":"0",
      "expectedWriterGeneration":"1",
      "policyId":"synthetic-policy-1",
      "suppressionUntil":"2026-10-01T00:00:00.0000000+00:00",
      "validUntil":"2026-09-30T00:00:00.0000000+00:00"
    }}"""

let private approval proposal =
    $"""{{"proposal":{proposal},"approvalId":"{approvalId}","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""

let private confirm =
    $"""{{"kind":"CONFIRM_MANAGED_PAYLOAD_ABSENCE","copy":{copy}}}"""

let private final =
    $"""{{"kind":"COMPLETE_SUPPRESSION_HORIZON","copy":{copy},"recoveryFenceDigest":"{digest}","oldWriterGeneration":"1","newWriterGeneration":"2"}}"""

let private exact () =
    match HttpTombstoneTerminalInput.approve (bytes (approval confirm)) with
    | Ok value ->
        Expect.equal value.ApprovalId (Guid.Parse approvalId) "Approval identity is exact"

        match value.Proposal with
        | TombstoneTerminalProposal.ConfirmManagedPayloadAbsence item ->
            Expect.equal item.CaseId (Guid.Parse caseId) "Opaque case identity is exact"
            Expect.equal item.RelevantCopyCount 0L "Relevant-copy count is exact"
            Expect.equal item.PolicyId "synthetic-policy-1" "Reviewed policy identity is exact"
        | _ -> failtest "Expected managed-copy absence proposal"
    | Error _ -> failtest "Exact managed-copy absence approval must decode"

    match HttpTombstoneTerminalInput.approve (bytes (approval final)) with
    | Ok value ->
        match value.Proposal with
        | TombstoneTerminalProposal.CompleteSuppressionHorizon item ->
            Expect.equal item.RecoveryFenceDigest digest "Final fence digest is mandatory"
            Expect.equal item.OldWriterGeneration 1L "Old generation is exact"
            Expect.equal item.NewWriterGeneration 2L "New generation is exact"
        | _ -> failtest "Expected final suppression-horizon proposal"
    | Error _ -> failtest "Exact final approval must decode"

let private malformed () =
    let reject source =
        match HttpTombstoneTerminalInput.approve (bytes source) with
        | Ok _ -> failtest "Malformed terminal approval was admitted"
        | Error _ -> ()

    reject (approval (final.Replace($"\"recoveryFenceDigest\":\"{digest}\",", "")))

    reject (
        approval (confirm.Replace("\"copy\":", $"\"recoveryFenceDigest\":\"{digest}\",\"copy\":"))
    )

    reject (approval (confirm.Replace("CONFIRM_MANAGED_PAYLOAD_ABSENCE", "ERASURE_FINAL")))
    reject (approval (confirm.Replace("\"relevantCopyCount\":\"0\"", "\"relevantCopyCount\":0")))
    reject (approval (confirm.Replace("synthetic-policy-1", " synthetic-policy-1")))
    reject (approval (confirm.Replace(digest, String.replicate 64 "A")))
    reject ((approval confirm).Replace("\"approvalId\":", "\"actorId\":\"forged\",\"approvalId\":"))

let tests =
    testList
        "terminal steward approval input"
        [
            testCase "[CC-WEB-001] terminal proposal tags bind exact owner evidence draft" exact
            testCase
                "[CC-WEB-001] terminal approval rejects missing proof and forged fields"
                malformed
        ]
