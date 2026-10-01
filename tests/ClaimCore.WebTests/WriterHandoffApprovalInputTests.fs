module ClaimCore.WebTests.WriterHandoffApprovalInputTests

open ClaimCore.Contracts

open System
open System.Text
open Expecto
open ClaimCore.Web

let private bytes (value: string) = Encoding.UTF8.GetBytes value
let private approval = "40000000-0000-4000-8000-000000000001"
let private handoff = "50000000-0000-4000-8000-000000000001"
let private signer = "60000000-0000-4000-8000-000000000001"
let private digest = String.replicate 64 "a"

let private valid =
    $"""{{"approvalId":"{approval}","handoffId":"{handoff}","oldGeneration":"2","expectedWitnessSequence":"41","expectedWitnessHash":"{digest}","newCapabilitySha256":"{digest}","checkpointSigningKeyId":"{signer}","fenceReportSha256":"{digest}","inventorySha256":"{digest}","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""

let private exact () =
    match HttpWriterHandoffApprovalInput.approve (bytes valid) with
    | Ok value ->
        Expect.equal value.ApprovalId (Guid.Parse approval) "Approval identity is exact"
        Expect.equal value.HandoffId (Guid.Parse handoff) "Handoff identity is exact"
        Expect.equal value.OldGeneration 2L "Old writer generation is exact"
        Expect.equal value.ExpectedWitnessSequence 41L "Witness cutoff is exact"

        Expect.equal
            value.NewCapabilitySha256
            (Array.create 32 0xaauy)
            "New capability hash is exact"

        Expect.equal value.CheckpointSigningKeyId (Guid.Parse signer) "Checkpoint signer is exact"
    | Error _ -> failtest "Exact writer handoff approval must decode"

let private refuses () =
    let reject source =
        match HttpWriterHandoffApprovalInput.approve (bytes source) with
        | Ok _ -> failtest "Malformed writer handoff approval was admitted"
        | Error _ -> ()

    reject (valid.Replace(digest, String.replicate 64 "A"))
    reject (valid.Replace("\"oldGeneration\":\"2\"", "\"oldGeneration\":2"))
    reject (valid.Replace("+00:00", "+02:00"))
    reject (valid.Replace("\"approvalId\":", "\"actorId\":\"forged\",\"approvalId\":"))

let tests =
    testList
        "writer handoff approval input"
        [
            testCase "[CC-WEB-001] handoff approval binds exact fence candidate" exact
            testCase "[CC-WEB-001] handoff approval refuses forged or malformed metadata" refuses
        ]
