module ClaimCore.Tests.FreshRecoveryFormatTests

open System
open System.Text
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.SignedRecoveryTestSupport

let private request =
    {
        OperationId = Guid.Parse("20000000-0000-4000-8000-000000000901")
        CaseReference = "FORMAT-001"
        ExpectedVersion = 0L
        Command = Command.Open registration
    }

let private canonical () = RequestRecord.encode request
let private text () = canonical () |> Encoding.UTF8.GetString

let private freshRoundTrip () =
    let bytes = canonical ()

    Expect.equal
        (RequestRecord.decode 65536 bytes)
        (Ok request)
        "Current request bytes decode exactly"

    let artifact, _ = SignedRecoveryTestSupport.source request

    match verify artifact with
    | Error _ -> failtest "Current signed envelope must verify"
    | Ok verified ->
        Expect.equal verified.OperationId request.OperationId "Operation identity is bound"
        Expect.equal verified.CanonicalRequest bytes "Exact request bytes are recovered"
        Expect.notEqual verified.CaseId Guid.Empty "Reserved case identity is required"

let private oldCanonical () =
    for replacement in
        [
            "\"protocolVersion\":2"
            "\"canonicalCommandFormat\":2"
            "\"canonicalCommandFormat\":4"
        ] do
        let bytes =
            (text ()).Replace("\"canonicalCommandFormat\":3", replacement)
            |> Encoding.UTF8.GetBytes

        let before = Array.copy bytes
        Expect.isError (RequestRecord.decode 65536 bytes) "Unsupported canonical request is refused"
        Expect.isError (verify bytes) "Raw request cannot be promoted into signed import"
        Expect.equal bytes before "No compatibility rewrite occurs"

let private oldEnvelope () =
    let old =
        """{"format":"claimcore-recovery","formatVersion":2,"installationId":"10000000-0000-4000-8000-000000000001"}"""
        |> Encoding.UTF8.GetBytes

    Expect.isError (verify old) "Plaintext v2 artifact is refused"
    Expect.isError (verify (canonical ())) "Unsigned canonical record is refused"

let private noncanonical () =
    let spaced = Encoding.UTF8.GetBytes(" " + text ())

    Expect.equal
        (RequestRecord.decode 65536 spaced)
        (Ok request)
        "Semantic decoder can inspect input"

    Expect.isError (verify spaced) "Import never silently signs equivalent raw bytes"

let private tampering () =
    let source, _ = SignedRecoveryTestSupport.source request
    let original = Encoding.UTF8.GetString(source)

    for changed in
        [
            original.Replace("\"epoch\":8", "\"epoch\":9")
            original.Replace("\"formatVersion\":3", "\"formatVersion\":2")
            original + " "
        ] do
        Expect.isError
            (verify (Encoding.UTF8.GetBytes changed))
            "Metadata or byte-layout tampering is refused"

[<Tests>]
let tests =
    testList
        "fresh recovery format boundary"
        [
            testCase
                "[CC-REC-001] current request and signed artifact preserve exact identity"
                freshRoundTrip
            testCase "[CC-REC-001] historical canonical requests are refused" oldCanonical
            testCase "[CC-REC-001] plaintext v2 and unsigned records are refused" oldEnvelope
            testCase "[CC-REC-001] raw noncanonical bytes are not imported" noncanonical
            testCase
                "[CC-REC-001] signed envelope metadata and layout tampering are refused"
                tampering
        ]
