module ClaimCore.WebTests.CopyAdoptionApprovalInputTests

open ClaimCore.Contracts

open System
open System.Text
open Expecto
open ClaimCore.Application
open ClaimCore.Web

let private bytes (value: string) = Encoding.UTF8.GetBytes value
let private digest = String.replicate 64 "a"

let private baseRequest origin =
    $"""{{
      "approvalId":"40000000-0000-4000-8000-000000000001",
      "adoptionEventId":"50000000-0000-4000-8000-000000000001",
      "copyId":"60000000-0000-4000-8000-000000000001",
      "caseId":"70000000-0000-4000-8000-000000000001",
      "origin":{origin},
      "ciphertextSha256":"{digest}",
      "ciphertextBytes":"512",
      "capturedAt":"2026-09-20T00:00:00.0000000+00:00",
      "retainUntil":"2026-10-20T00:00:00.0000000+00:00",
      "locationCommitment":"{digest}",
      "custodianCommitment":"{digest}",
      "custodianSigningKeyId":"80000000-0000-4000-8000-000000000001",
      "registrySigningKeyId":"90000000-0000-4000-8000-000000000001",
      "inspectorSigningKeyId":"a0000000-0000-4000-8000-000000000001",
      "custodianCanonicalSha256":"{digest}",
      "registryCanonicalSha256":"{digest}",
      "inspectionReportSha256":"{digest}",
      "expiresAt":"2026-10-01T00:00:00.0000000+00:00"
    }}"""

let private productOrigin =
    $"""{{"kind":"PRODUCT_EXPORT","exportId":"b0000000-0000-4000-8000-000000000001","receiptSequence":"41","receiptHash":"{digest}"}}"""

let private externalOrigin =
    $"""{{"kind":"ADOPTED_EXTERNAL","registrySequence":"39","registryHash":"{digest}"}}"""

let private exact () =
    match HttpCopyAdoptionApprovalInput.approve (bytes (baseRequest productOrigin)) with
    | Ok value ->
        Expect.equal value.CiphertextBytes 512L "Exact ciphertext size"
        Expect.equal value.CiphertextSha256 (Array.create 32 0xaauy) "Exact digest"

        match value.Origin with
        | CopyAdoptionOrigin.ProductExport(exportId, sequence, hash) ->
            Expect.equal exportId (Guid.Parse "b0000000-0000-4000-8000-000000000001") "Export ID"
            Expect.equal sequence 41L "Witnessed export receipt sequence"
            Expect.equal hash (Array.create 32 0xaauy) "Witnessed export receipt hash"
        | _ -> failtest "Expected product-export provenance"
    | Error _ -> failtest "Exact product-export approval must decode"

    match HttpCopyAdoptionApprovalInput.approve (bytes (baseRequest externalOrigin)) with
    | Ok value ->
        match value.Origin with
        | CopyAdoptionOrigin.AdoptedExternal(sequence, hash) ->
            Expect.equal sequence 39L "Pre-fence registry sequence"
            Expect.equal hash (Array.create 32 0xaauy) "Pre-fence registry hash"
        | _ -> failtest "Expected external-copy provenance"
    | Error _ -> failtest "Exact external-copy approval must decode"

let private malformed () =
    let reject source =
        match HttpCopyAdoptionApprovalInput.approve (bytes source) with
        | Ok _ -> failtest "Malformed adoption approval was admitted"
        | Error _ -> ()

    reject (baseRequest (productOrigin.Replace("PRODUCT_EXPORT", "UNMANAGED")))

    reject (
        baseRequest (
            productOrigin.Replace("\"exportId\":", "\"registryHash\":\"forged\",\"exportId\":")
        )
    )

    reject (
        baseRequest (
            externalOrigin.Replace("\"registryHash\":", "\"exportId\":\"forged\",\"registryHash\":")
        )
    )

    reject (
        (baseRequest productOrigin)
            .Replace("\"ciphertextBytes\":\"512\"", "\"ciphertextBytes\":512")
    )

    reject ((baseRequest productOrigin).Replace(digest, String.replicate 64 "A"))

    reject (
        (baseRequest productOrigin)
            .Replace("\"approvalId\":", "\"actorId\":\"forged\",\"approvalId\":")
    )

let tests =
    testList
        "copy adoption approval input"
        [
            testCase "[CC-WEB-001] adoption origin binds exact witnessed provenance" exact
            testCase
                "[CC-WEB-001] adoption approval refuses invented provenance and actor fields"
                malformed
        ]
