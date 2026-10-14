module ClaimCore.Tests.OperationRecordTests

open System
open System.Text
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.RecordFormat

let private operationId = Guid.Parse "20000000-0000-4000-8000-000000000001"

let private steward action : StewardshipRequest =
    {
        OperationId = operationId
        CaseReference = "UNIT-001"
        ExpectedVersion = 7L
        Action = action
    }

let private authority action : AuthorityRequestRecord =
    {
        EventId = operationId
        Target =
            {
                Kind = "HUMAN"
                Issuer = "https://issuer.example/"
                Subject = "synthetic-owner"
            }
        Action = action
    }

let private stewardshipProperty =
    property {
        let! reason = Gen.string (Range.constant 1 200) Gen.alpha

        let! action =
            Gen.item
                [
                    StewardshipAction.Void reason
                    StewardshipAction.Reinstate reason
                    StewardshipAction.Erase
                ]

        let request = steward action
        return StewardshipRecord.decode 65536 (StewardshipRecord.encode request) = Ok request
    }

let private authorityProperty =
    property {
        let! active = Gen.bool

        let! scope =
            Gen.item
                [ RecordedGrantScope.Installation; RecordedGrantScope.CaseReference "UNIT-001" ]

        let! role =
            Gen.item [ "OWNER"; "CASE_READER"; "CASE_EDITOR"; "DATA_STEWARD"; "AUDITOR" ]

        let! action =
            Gen.item
                [
                    RecordedAuthorityAction.RegisterActor
                    RecordedAuthorityAction.SetGrant(role, scope, active)
                    RecordedAuthorityAction.SetEnabled active
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
            Fixtures.request 0L (Command.Open Fixtures.registration) |> RequestRecord.encode
            Fixtures.opened () |> Claim.view |> CaseRecord.encodeSnapshot
            steward (StewardshipAction.Void "Reason") |> StewardshipRecord.encode
            authority (
                RecordedAuthorityAction.SetGrant(
                    "CASE_EDITOR",
                    RecordedGrantScope.Installation,
                    true
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
            && total (StewardshipRecord.decode 65536) bytes
            && total (AuthorityRecord.decode 65536) bytes
            && total (LedgerEntry.decodePayload 65536) bytes
    }

let private strictRecords () =
    let canonical =
        steward StewardshipAction.Erase
        |> StewardshipRecord.encode
        |> Encoding.UTF8.GetString

    let variants =
        [
            canonical.Replace("\"type\":\"ERASE\"", "\"type\":\"ERASE\",\"reason\":\"forbidden\"")
            canonical.Replace("\"type\":\"ERASE\"", "\"type\":\"UNKNOWN\"")
            canonical.Replace(
                "\"expectedVersion\":7",
                "\"expectedVersion\":7,\"expectedVersion\":7"
            )
            canonical.Replace("\"caseReference\":\"UNIT-001\"", "\"caseReference\":null")
            canonical.Replace("\"caseReference\":\"UNIT-001\"", "\"caseReference\":\"\\ud800\"")
            canonical.Replace(operationId.ToString(), Guid.Empty.ToString())
            canonical.Replace(operationId.ToString(), operationId.ToString("N"))
            canonical + " trailing"
        ]

    for bytes in variants |> List.map Encoding.UTF8.GetBytes do
        Expect.isError (StewardshipRecord.decode 65536 bytes) "Invalid record is refused"

    let grant =
        authority (
            RecordedAuthorityAction.SetGrant("CASE_EDITOR", RecordedGrantScope.Installation, true)
        )

    let encoded = AuthorityRecord.encode grant |> Encoding.UTF8.GetString

    for candidate in
        [
            encoded.Replace("\"active\":true", "\"active\":\"true\"")
            encoded.Replace("\"type\":\"INSTALLATION\"", "\"type\":\"UNKNOWN\"")
            encoded.Replace("\"type\":\"SET_GRANT\"", "\"type\":\"UNKNOWN\"")
            encoded.Replace("\"role\":\"CASE_EDITOR\",", "")
            encoded.Replace("\"kind\":\"HUMAN\"", "\"kind\":\"HUMAN\",\"extra\":true")
        ] do
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
                    "{\"operationId\":\"20000000-0000-4000-8000-000000000001\",\"caseReference\":\"UNIT-001\",\"expectedVersion\":7,\"action\":{\"type\":\"ERASE\"}}"

                Expect.equal
                    (steward StewardshipAction.Erase
                     |> StewardshipRecord.encode
                     |> Encoding.UTF8.GetString)
                    expected
                    "Canonical field order")
            testCase "authority encoding has fixed independent bytes" (fun () ->
                let expected =
                    "{\"eventId\":\"20000000-0000-4000-8000-000000000001\",\"target\":{\"kind\":\"HUMAN\",\"issuer\":\"https://issuer.example/\",\"subject\":\"synthetic-owner\"},\"action\":{\"type\":\"REGISTER_ACTOR\"}}"

                Expect.equal
                    (authority RecordedAuthorityAction.RegisterActor
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
                let encoded = steward StewardshipAction.Erase |> StewardshipRecord.encode

                Expect.isError
                    (StewardshipRecord.decode (encoded.Length - 1) encoded)
                    "Over byte limit"

                Expect.isOk (StewardshipRecord.decode encoded.Length encoded) "Exact byte limit"

                for bytes in [ [| 0xFFuy |]; [||]; Unchecked.defaultof<byte array> ] do
                    Expect.isError
                        (StewardshipRecord.decode 65536 bytes)
                        "Typed stewardship refusal"

                    Expect.isError (AuthorityRecord.decode 65536 bytes) "Typed authority refusal")
            testCase
                "all durable decoders are total on arbitrary and mutated valid bytes"
                (fun () -> PropertyHarness.run "CC-FUZZ-RECORD-001" decoderProperty)
        ]
