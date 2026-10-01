module ClaimCore.WebTests.CopyDeletionApprovalInputTests

open ClaimCore.Contracts

open System
open System.Text
open Expecto
open ClaimCore.Web

let private bytes (value: string) = Encoding.UTF8.GetBytes value
let private approval = "40000000-0000-4000-8000-000000000001"
let private eventId = "50000000-0000-4000-8000-000000000001"
let private copyId = "60000000-0000-4000-8000-000000000001"
let private verifier = "70000000-0000-4000-8000-000000000001"
let private digest = String.replicate 64 "a"

let private valid =
    $"""{{"approvalId":"{approval}","deletionEventId":"{eventId}","copyId":"{copyId}","verifierSigningKeyId":"{verifier}","expectedCopyRevision":"2","locationCommitment":"{digest}","inspectionReportSha256":"{digest}","witnessCutoffSequence":"41","witnessCutoffHash":"{digest}","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""

let private exact () =
    match HttpCopyDeletionApprovalInput.approve (bytes valid) with
    | Ok value ->
        Expect.equal value.ApprovalId (Guid.Parse approval) "Approval identity is exact"
        Expect.equal value.DeletionEventId (Guid.Parse eventId) "Delete event is exact"
        Expect.equal value.CopyId (Guid.Parse copyId) "Copy identity is exact"

        Expect.equal
            value.VerifierSigningKeyId
            (Guid.Parse verifier)
            "Registered verifier key is exact"

        Expect.equal value.ExpectedCopyRevision 2L "Expected copy revision is exact"
        Expect.equal value.WitnessCutoffSequence 41L "Witness cutoff is exact"
        Expect.equal value.InspectionReportSha256 (Array.create 32 0xaauy) "Report digest is exact"
    | Error _ -> failtest "Exact deletion approval must decode"

let private refuses () =
    let reject source =
        match HttpCopyDeletionApprovalInput.approve (bytes source) with
        | Ok _ -> failtest "Malformed deletion approval was admitted"
        | Error _ -> ()

    reject (valid.Replace(digest, String.replicate 64 "A"))
    reject (valid.Replace("\"expectedCopyRevision\":\"2\"", "\"expectedCopyRevision\":2"))
    reject (valid.Replace("+00:00", "+02:00"))
    reject (valid.Replace("\"approvalId\":", "\"actorId\":\"forged\",\"approvalId\":"))

    reject (
        valid.Replace(
            "\"verifierSigningKeyId\":",
            "\"holderActorId\":\"forged\",\"verifierSigningKeyId\":"
        )
    )

let tests =
    testList
        "copy deletion approval input"
        [
            testCase "[CC-WEB-001] deletion approval binds exact report verifier and cutoff" exact
            testCase "[CC-WEB-001] deletion approval refuses forged or malformed metadata" refuses
        ]
