module ClaimCore.WebTests.SignerApprovalInputTests

open ClaimCore.Contracts

open System
open System.Text
open Expecto
open ClaimCore.Application
open ClaimCore.Web

let private bytes (value: string) = Encoding.UTF8.GetBytes(value)
let private approval = "40000000-0000-4000-8000-000000000001"
let private key = "50000000-0000-4000-8000-000000000001"
let private digest = String.replicate 64 "a"

let private valid =
    $"""{{"approvalId":"{approval}","signingKeyId":"{key}","action":"REGISTER","purpose":"COPY_ATTESTOR","publicKeySha256":"{digest}","approvalRole":"CUSTODIAN","holderApprovalId":null,"expiresAt":"2026-10-01T00:00:00.0000000+00:00"}}"""

let private exact () =
    match HttpSignerApprovalInput.approve (bytes valid) with
    | Ok value ->
        Expect.equal value.ApprovalId (Guid.Parse approval) "Stable approval identity"
        Expect.equal value.SigningKeyId (Guid.Parse key) "Exact signer identity"
        Expect.equal value.Action CopySignerAction.Register "Closed signer action"
        Expect.equal value.Purpose CopySignerPurpose.CopyAttestor "Closed signer purpose"
        Expect.equal value.Role CopySignerApprovalRole.Custodian "Requested capacity only"
        Expect.equal value.PublicKeySha256 (Array.create 32 0xaauy) "Exact key digest"
    | Error _ -> failtest "Exact signer approval must decode"

    match
        HttpSignerApprovalInput.approve (
            bytes (valid.Replace("COPY_ATTESTOR", "WRITER_HANDOFF_ABORT"))
        )
    with
    | Ok value ->
        Expect.equal
            value.Purpose
            CopySignerPurpose.WriterHandoffAbort
            "Abort signatures have a dedicated closed purpose"
    | Error _ -> failtest "Exact abort signer purpose must decode"

    match
        HttpSignerApprovalInput.approve (
            bytes (valid.Replace("COPY_ATTESTOR", "INSTALLATION_LOSS_RETIREMENT"))
        )
    with
    | Ok value ->
        Expect.equal
            value.Purpose
            CopySignerPurpose.InstallationLossRetirement
            "Loss retirement signatures have a dedicated closed purpose"
    | Error _ -> failtest "Exact loss-retirement signer purpose must decode"

    match
        HttpSignerApprovalInput.approve (
            bytes (valid.Replace("COPY_ATTESTOR", "RESTORE_COPY_VERIFIER"))
        )
    with
    | Ok value ->
        Expect.equal
            value.Purpose
            CopySignerPurpose.RestoreCopyVerifier
            "Physical copy restore signatures have a dedicated closed purpose"
    | Error _ -> failtest "Exact restore-copy verifier purpose must decode"

let private rejects () =
    let refused source =
        match HttpSignerApprovalInput.approve (bytes source) with
        | Ok _ -> failtest "Malformed signer approval was admitted"
        | Error _ -> ()

    refused (valid.Replace("REGISTER", "INVENTED_ACTION"))
    refused (valid.Replace("CUSTODIAN", "ARBITRARY_ACTOR"))
    refused (valid.Replace("COPY_ATTESTOR", "ARBITRARY_PURPOSE"))
    refused (valid.Replace("\"holderApprovalId\":null", $"\"holderApprovalId\":\"{key}\""))
    refused (valid.Replace(digest, String.replicate 64 "A"))
    refused (valid.Replace("+00:00", "+02:00"))
    refused (valid.Replace(".0000000+00:00", ".0000001+00:00"))
    refused (valid.Replace("\"approvalId\":", "\"actorId\":\"private\",\"approvalId\":"))
    refused (valid.Replace("\"approvalId\":", $"\"approvalId\":\"{approval}\",\"approvalId\":"))

let tests =
    testList
        "signer approval input"
        [
            testCase "[CC-WEB-001] signer approval binds exact key and expiry" exact
            testCase "[CC-WEB-001] signer approval refuses forged wire fields" rejects
        ]
