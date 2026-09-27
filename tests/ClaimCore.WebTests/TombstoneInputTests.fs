module ClaimCore.WebTests.TombstoneInputTests

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

let private proposal =
    $"""{{"eventId":"{eventId}","caseId":"{caseId}","purgeEventId":"{approvalId}","purgeWitnessSequence":"11","purgeWitnessEpoch":"1","purgeWitnessHash":"{digest}","cutoffSequence":"12","cutoffHash":"{digest}","targetCount":"8","targetDigest":"{digest}","expectedAuthorityRevision":"2","expectedAuthorityHash":"{digest}","validUntil":"2026-10-01T00:00:00.0000000+00:00"}}"""

let private approval =
    $"""{{"proposal":{proposal},"approvalId":"{approvalId}","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""

let private hold mutation =
    $"""{{"eventId":"{eventId}","caseId":"{caseId}","expectedAuthorityRevision":"2","expectedAuthorityHash":"{digest}","mutation":{mutation}}}"""

let private reject parse source =
    match parse (bytes source) with
    | Ok _ -> failtest "Malformed tombstone input was admitted"
    | Error _ -> ()

let private reviewAndApproval () =
    match HttpTombstoneInput.review (bytes $"""{{"caseId":"{caseId}"}}""") with
    | Ok id -> Expect.equal id (Guid.Parse caseId) "Only an opaque case identity is accepted"
    | Error _ -> failtest "Exact opaque review target must decode"

    match HttpTombstoneInput.approve (bytes approval) with
    | Ok value ->
        Expect.equal value.ApprovalId (Guid.Parse approvalId) "Approval identity is exact"
        Expect.equal value.Proposal.CaseId (Guid.Parse caseId) "Opaque target is exact"
        Expect.equal value.Proposal.CutoffSequence 12L "Witness cutoff is exact"
        Expect.equal value.Proposal.TargetCount 8L "Closed target count is exact"
    | Error _ -> failtest "Exact prune approval must decode"

let private holdCodes () =
    let record =
        hold
            $"""{{"kind":"RECORD","holdId":"{approvalId}","groundCode":"LEGAL_RETENTION","reviewOn":"2026-10-01"}}"""

    match HttpTombstoneInput.hold (bytes record) with
    | Ok value ->
        match value.Mutation with
        | TombstoneHoldMutation.Record(_, code, _) ->
            Expect.equal code "LEGAL_RETENTION" "Closed nonpayload ground is retained"
        | _ -> failtest "Wrong hold mutation decoded"
    | Error _ -> failtest "Exact hold record must decode"

    let release =
        hold $"""{{"kind":"RELEASE","holdId":"{approvalId}","releaseCode":"LEGAL_RELEASE"}}"""

    match HttpTombstoneInput.hold (bytes release) with
    | Ok value ->
        match value.Mutation with
        | TombstoneHoldMutation.Release(_, code) ->
            Expect.equal code "LEGAL_RELEASE" "Closed release code is retained"
        | _ -> failtest "Wrong hold release decoded"
    | Error _ -> failtest "Exact hold release must decode"

let private malformed () =
    reject HttpTombstoneInput.review $"""{{"caseId":"{caseId}","caseReference":"PRIVATE"}}"""
    reject HttpTombstoneInput.approve (approval.Replace(digest, String.replicate 64 "A"))

    reject
        HttpTombstoneInput.approve
        (approval.Replace("\"targetCount\":\"8\"", "\"targetCount\":8"))

    reject
        HttpTombstoneInput.approve
        (approval.Replace("\"proposal\":", "\"actorId\":\"forged\",\"proposal\":"))

    reject
        HttpTombstoneInput.hold
        (hold
            $"""{{"kind":"RECORD","holdId":"{approvalId}","groundCode":"CLAIMANT_NAME","reviewOn":"2026-10-01"}}""")

    reject
        HttpTombstoneInput.hold
        (hold
            $"""{{"kind":"RELEASE","holdId":"{approvalId}","releaseCode":"OTHER","reason":"PRIVATE"}}""")

let tests =
    testList
        "tombstone input"
        [
            testCase
                "[CC-WEB-001] opaque review and prune approval retain exact evidence"
                reviewAndApproval
            testCase "[CC-WEB-001] tombstone holds accept only closed nonpayload codes" holdCodes
            testCase "[CC-WEB-001] tombstone inputs reject private and malformed fields" malformed
        ]
