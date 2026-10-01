module ClaimCore.WebTests.RealDataActivationInputTests

open ClaimCore.Contracts

open System
open System.Text
open Expecto
open ClaimCore.Web

let private bytes (value: string) = Encoding.UTF8.GetBytes value
let private approval = "40000000-0000-4000-8000-000000000001"
let private plan = "50000000-0000-4000-8000-000000000001"
let private activation = "60000000-0000-4000-8000-000000000001"
let private installation = "70000000-0000-4000-8000-000000000001"
let private lineage = "80000000-0000-4000-8000-000000000001"
let private digest = String.replicate 64 "a"

let private valid =
    $"""{{"approvalId":"{approval}","planId":"{plan}","activationId":"{activation}","installationId":"{installation}","lineageId":"{lineage}","epoch":"1","writerGeneration":"2","activationPlanSha256":"{digest}","policySha256":"{digest}","reviewWitnessSequence":"41","reviewWitnessHash":"{digest}","expectedWitnessSequence":"41","expectedWitnessHash":"{digest}","expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""

let private exact () =
    match HttpRealDataActivationInput.approve (bytes valid) with
    | Ok value ->
        Expect.equal value.ApprovalId (Guid.Parse approval) "Approval identity is exact."
        Expect.equal value.PlanId (Guid.Parse plan) "Published plan identity is exact."
        Expect.equal value.ActivationId (Guid.Parse activation) "Activation identity is exact."
        Expect.equal value.ReviewWitnessSequence 41L "Historical review anchor is exact."
        Expect.equal value.ExpectedWitnessSequence 41L "Current approval-chain tip is exact."
        Expect.equal value.ActivationPlanSha256 (Array.create 32 0xaauy) "Plan digest is exact."
    | Error _ -> failtest "Exact actor approval did not decode."

    match HttpRealDataActivationInput.review (bytes $"""{{"planId":"{plan}"}}""") with
    | Ok value ->
        Expect.equal value (Guid.Parse plan) "Review binds only an opaque published plan ID."
    | Error _ -> failtest "Exact plan review did not decode."

let private refuses () =
    let reject source =
        match HttpRealDataActivationInput.approve (bytes source) with
        | Ok _ -> failtest "Forged or malformed activation approval was admitted."
        | Error _ -> ()

    reject (valid.Replace(digest, String.replicate 64 "A"))
    reject (valid.Replace("\"writerGeneration\":\"2\"", "\"writerGeneration\":2"))
    reject (valid.Replace("+00:00", "+02:00"))
    reject (valid.Replace("\"approvalId\":", "\"approverActorId\":\"forged\",\"approvalId\":"))
    reject (valid.Replace("\"approvalId\":", "\"signingKeyFile\":\"forged\",\"approvalId\":"))

    match
        HttpRealDataActivationInput.review (bytes $"""{{"planId":"{plan}","actorId":"forged"}}""")
    with
    | Ok _ -> failtest "Review admitted a caller-supplied actor identity."
    | Error _ -> ()

let tests =
    testList
        "real-data activation input"
        [
            testCase
                "[CC-WEB-001] activation review and approval bind exact published plan fields"
                exact
            testCase "[CC-WEB-001] activation requests refuse forged actor or key metadata" refuses
        ]
