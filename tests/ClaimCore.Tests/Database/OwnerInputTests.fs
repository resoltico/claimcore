module ClaimCore.Tests.OwnerInputTests

open System
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Database

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

[<Tests>]
let tests =
    testList
        "private owner input admission"
        [
            testCase "exact owner format retains identity and caller buffer ownership" accepted
            testCase "format and principal failures retain the refusal boundary" refused
        ]
