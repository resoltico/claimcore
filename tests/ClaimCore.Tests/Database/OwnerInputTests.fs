module ClaimCore.Tests.OwnerInputTests

open ClaimCore.Domain

open System
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Database
open ClaimCore.Protocol
open ClaimCore.Json
open ClaimCore.Postgres

let private operation = Guid.Parse "10000000-0000-4000-8000-000000000001"

let private owner issuer subject =
    JsonSerializer.SerializeToUtf8Bytes
        {|
            operationId = operation
            issuer = issuer
            subject = subject
        |}

let private accepted () =
    let bytes = owner "https://issuer.example/" "human-owner"
    let original = Array.copy bytes
    let id, principal = Inputs.decodeOwner bytes
    Expect.equal id operation "Operation identity retained"
    Expect.isTrue (PrincipalKey.isHuman principal) "The format denotes a human owner"

    Expect.equal
        (PrincipalKey.describe principal).Subject
        "human-owner"
        "Immutable identity retained"

    Expect.equal bytes original "Buffer ownership remains with the caller"

let private refused () =
    let malformed =
        [
            [| 255uy |]
            Encoding.UTF8.GetBytes "not-json"
            Encoding.UTF8.GetBytes "{}"
            JsonSerializer.SerializeToUtf8Bytes
                {|
                    operationId = Guid.Empty
                    issuer = "https://issuer.example/"
                    subject = "owner"
                |}
            JsonSerializer.SerializeToUtf8Bytes
                {|
                    operationId = operation
                    issuer = "https://issuer.example/"
                    subject = "owner"
                    kind = "SERVICE"
                |}
            owner "http://issuer.example/" "owner"
            owner "https://issuer.example/?query=1" "owner"
            owner "https://issuer.example/" " "
            owner "https://issuer.example/" (String.replicate 513 "A")
        ]

    for bytes in malformed do
        let original = Array.copy bytes

        Expect.throwsT<InvalidOperationException>
            (fun () -> Inputs.decodeOwner bytes |> ignore)
            "Malformed format or invalid human principal remains a refusal"

        Expect.equal bytes original "Failed decoding leaves caller-owned bytes intact"

let private replies () =
    let reason =
        Refusal.InvalidInput(InputTarget.PrincipalSubject, Violation.TooLong 512)

    let bytes, code = Replies.authority (ProvisionOwnerOutcome.Rejected reason)
    use document = JsonDocument.Parse(bytes: byte array)
    Expect.equal code 2 "A refusal uses the shared rejected exit"
    Expect.equal (document.RootElement.GetProperty("kind").GetString()) "REJECTED" "Literal outcome"
    let diagnostic = document.RootElement.GetProperty("diagnostic")
    Expect.equal (diagnostic.GetProperty("code").GetString()) "TOO_LONG" "The cause is retained"

    Expect.equal
        (diagnostic.GetProperty("target").GetString())
        "principal.subject"
        "The input target is retained"

    Expect.equal
        (diagnostic.GetProperty("maximumCharacters").GetInt32())
        512
        "Typed parameters are retained"

    Expect.equal
        (Codec.decode 10000 Replies.codec bytes)
        (Ok(Reply.Rejected reason))
        "The local codec reads its own complete reply"

    let bytes, code =
        Replies.authority (ProvisionOwnerOutcome.Failed CoreFault.SchemaMismatch)

    use document = JsonDocument.Parse(bytes: byte array)
    Expect.equal code 3 "A known core failure uses the shared failed exit"

    Expect.equal
        (document.RootElement.GetProperty("fault").GetProperty("code").GetString())
        "CORE_SCHEMA_MISMATCH"
        "The fault is retained"

let private usageAndExits () =
    let usage =
        match Inputs.command [ "initialize" ] with
        | Error usage -> usage
        | Ok _ -> failtest "A missing zone must produce usage"

    Expect.equal usage.Issue UsageIssue.ArgumentsInvalid "Typed grammar error"
    let bytes, code = Replies.usage usage
    use document = JsonDocument.Parse(bytes: byte array)
    Expect.equal code 2 "Usage is a definite refusal"

    Expect.equal
        (document.RootElement.GetProperty("kind").GetString())
        "USAGE_ERROR"
        "Usage is distinct from a domain refusal"

    Expect.equal
        (document.RootElement.GetProperty("usage").[0].GetString())
        "claimcore-database initialize TIME_ZONE"
        "An actionable synopsis is supplied"

    for code, expected in
        [
            ExitCode.Success, 0
            ExitCode.Rejected, 2
            ExitCode.Failed, 3
            ExitCode.OutcomeUnknown, 4
            ExitCode.Diverged, 5
            ExitCode.Usage, 64
            ExitCode.InternalError, 70
            ExitCode.Cancelled, 130
        ] do
        Expect.equal (ExitCode.value code) expected "Independent literal exit number"

[<Tests>]
let tests =
    testList
        "private owner input admission"
        [
            testCase "owner replies preserve refusal cause fields and core faults" replies
            testCase "typed command usage and shared exit numbers remain explicit" usageAndExits
            testCase "exact owner format retains identity and caller buffer ownership" accepted
            testCase "format and principal failures retain the refusal boundary" refused
        ]
