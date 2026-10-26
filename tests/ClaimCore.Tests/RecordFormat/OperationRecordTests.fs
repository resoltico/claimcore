module ClaimCore.Tests.OperationRecordTests

open ClaimCore.Contract

open System
open System.Text
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.RecordFormat

let private operationId = Guid.Parse "20000000-0000-4000-8000-000000000001"

let private steward action : UnvalidatedCaseRequest =
    {
        OperationId = operationId
        CaseReference = "UNIT-001"
        ExpectedRevision = 7L
        Act = action
    }

let private authority action : AuthorityRequestRecord =
    {
        OperationId = operationId
        Target =
            {
                Kind = "HUMAN"
                Issuer = "https://issuer.example/"
                Subject = "synthetic-owner"
            }
        Act = action
    }

let private stewardshipProperty =
    property {
        let! reason = Gen.string (Range.constant 1 200) Gen.alpha

        let! action =
            Gen.item [ CaseAct.Void reason; CaseAct.Reinstate reason; CaseAct.Erase ]

        let request = steward action
        return RequestRecord.decode 65536 (RequestRecord.encode request) = Ok request
    }

let private authorityProperty =
    property {
        let! active = Gen.bool

        let! scope =
            Gen.item [ GrantTarget.Installation; GrantTarget.CaseReference "UNIT-001" ]

        let! role =
            Gen.item
                [
                    "OWNER"
                    "CASE_READER"
                    "CASE_REGISTRAR"
                    "CASE_HANDLER"
                    "PAYMENT_RECORDER"
                    "DATA_STEWARD"
                    "AUDITOR"
                ]

        let! action =
            Gen.item
                [
                    RecordedAuthorityAct.Operation(ClaimCore.Contract.AuthorityAct.Register)
                    RecordedAuthorityAct.Operation(
                        (if active then
                             ClaimCore.Contract.AuthorityAct.Grant
                         else
                             ClaimCore.Contract.AuthorityAct.Revoke)
                            { Role = role; Scope = scope }
                    )
                    RecordedAuthorityAct.Operation(
                        (if active then
                             ClaimCore.Contract.AuthorityAct.Enable
                         else
                             ClaimCore.Contract.AuthorityAct.Disable)
                    )
                ]

        let request = authority action
        return AuthorityRecord.decode 65536 (AuthorityRecord.encode request) = Ok request
    }

let private total decode bytes =
    try
        decode bytes |> ignore
        true
    with _ ->
        false

let private decoderProperty =
    let seeds =
        [
            Fixtures.request 0L (CaseAct.Open Fixtures.registration) |> RequestRecord.encode
            Fixtures.opened () |> Case.snapshot |> CaseRecord.encodeSnapshot
            steward (CaseAct.Void "Reason") |> RequestRecord.encode
            authority (
                RecordedAuthorityAct.Operation(
                    ClaimCore.Contract.AuthorityAct.Grant
                        {
                            Role = "CASE_HANDLER"
                            Scope = GrantTarget.Installation
                        }
                )
            )
            |> AuthorityRecord.encode
            LedgerEntry.encodePayload
                {
                    Request = [| 1uy |]
                    Snapshot = [| 2uy |]
                }
        ]

    let generator =
        Gen.choice (DecoderCorpora.jsonLike :: List.map DecoderCorpora.mutated seeds)

    property {
        let! bytes = generator

        return
            total (RequestRecord.decode 65536) bytes
            && total CaseRecord.decodeSnapshot bytes
            && total (AuthorityRecord.decode 65536) bytes
            && total (LedgerEntry.decodePayload 65536) bytes
    }

let private strictRecords () =
    let canonical =
        steward CaseAct.Erase |> RequestRecord.encode |> Encoding.UTF8.GetString

    let variants =
        [
            canonical.Replace("\"kind\":\"ERASE\"", "\"kind\":\"ERASE\",\"reason\":\"forbidden\"")
            canonical.Replace("\"kind\":\"ERASE\"", "\"kind\":\"UNKNOWN\"")
            canonical.Replace(
                "\"expectedRevision\":7",
                "\"expectedRevision\":7,\"expectedRevision\":7"
            )
            canonical.Replace("\"caseReference\":\"UNIT-001\"", "\"caseReference\":null")
            canonical.Replace("\"caseReference\":\"UNIT-001\"", "\"caseReference\":\"\\ud800\"")
            canonical.Replace(operationId.ToString(), Guid.Empty.ToString())
            canonical.Replace(operationId.ToString(), operationId.ToString("N"))
            canonical + " trailing"
        ]

    for bytes in variants |> List.map Encoding.UTF8.GetBytes do
        Expect.isFalse
            (bytes = Encoding.UTF8.GetBytes canonical)
            "Negative control changes the record"

        Expect.isError (RequestRecord.decode 65536 bytes) "Invalid record is refused"

    let grant =
        authority (
            RecordedAuthorityAct.Operation(
                ClaimCore.Contract.AuthorityAct.Grant
                    {
                        Role = "CASE_HANDLER"
                        Scope = GrantTarget.Installation
                    }
            )
        )

    let encoded = AuthorityRecord.encode grant |> Encoding.UTF8.GetString

    for candidate in
        [
            encoded.Replace("\"kind\":\"GRANT\"", "\"kind\":\"GRANT\",\"active\":true")
            encoded.Replace("\"kind\":\"INSTALLATION\"", "\"kind\":\"UNKNOWN\"")
            encoded.Replace("\"kind\":\"GRANT\"", "\"kind\":\"UNKNOWN\"")
            encoded.Replace("\"role\":\"CASE_HANDLER\",", "")
            encoded.Replace("\"kind\":\"HUMAN\"", "\"kind\":\"HUMAN\",\"extra\":true")
        ] do
        Expect.notEqual candidate encoded "Negative control changes the authority record"

        Expect.isError
            (AuthorityRecord.decode 65536 (Encoding.UTF8.GetBytes candidate))
            "Strict authority shape"

[<Tests>]
let tests =
    testList
        "canonical operation records"
        [
            testCase "stewardship encoding has fixed independent bytes" (fun () ->
                let expected =
                    "{\"act\":{\"kind\":\"ERASE\"},\"caseReference\":\"UNIT-001\",\"expectedRevision\":7,\"format\":1,\"kind\":\"CASE\",\"operationId\":\"20000000-0000-4000-8000-000000000001\"}"

                Expect.equal
                    (steward CaseAct.Erase |> RequestRecord.encode |> Encoding.UTF8.GetString)
                    expected
                    "Canonical field order")
            testCase "authority encoding has fixed independent bytes" (fun () ->
                let expected =
                    "{\"act\":{\"kind\":\"REGISTER\"},\"format\":1,\"kind\":\"AUTHORITY\",\"operationId\":\"20000000-0000-4000-8000-000000000001\",\"target\":{\"issuer\":\"https://issuer.example/\",\"kind\":\"HUMAN\",\"subject\":\"synthetic-owner\"}}"

                Expect.equal
                    (authority (
                        RecordedAuthorityAct.Operation(ClaimCore.Contract.AuthorityAct.Register)
                     )
                     |> AuthorityRecord.encode
                     |> Encoding.UTF8.GetString)
                    expected
                    "Canonical field order")
            testCase "all stewardship records round trip" (fun () ->
                PropertyHarness.run "CC-PROP-STEWARDSHIP-RECORD-001" stewardshipProperty)
            testCase "all authority action and scope records round trip" (fun () ->
                PropertyHarness.run "CC-PROP-AUTHORITY-RECORD-001" authorityProperty)
            testCase "strict record decoders reject hostile shapes" strictRecords
            testCase "record limits and malformed bytes are refused" (fun () ->
                let encoded = steward CaseAct.Erase |> RequestRecord.encode

                Expect.isError
                    (RequestRecord.decode (encoded.Length - 1) encoded)
                    "Over byte limit"

                Expect.isOk (RequestRecord.decode encoded.Length encoded) "Exact byte limit"

                for bytes in [ [| 0xFFuy |]; [||]; Unchecked.defaultof<byte array> ] do
                    Expect.isError (RequestRecord.decode 65536 bytes) "Typed stewardship refusal"

                    Expect.isError (AuthorityRecord.decode 65536 bytes) "Typed authority refusal")
            testCase
                "all durable decoders are total on arbitrary and mutated valid bytes"
                (fun () -> PropertyHarness.run "CC-FUZZ-RECORD-001" decoderProperty)
        ]
