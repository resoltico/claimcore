module ClaimCore.Tests.SchemaFormatTests

open System
open System.Text.Json
open Expecto
open ClaimCore.Wire

let private admits codec (value: string) =
    Codec.decode 4096 codec (JsonSerializer.SerializeToUtf8Bytes value)
    |> Result.isOk

let private timestamps () =
    for raw in [ "2026-10-05T03:00:00.000000Z"; "2024-02-29T03:00:00.123456Z" ] do
        Expect.isTrue (admits Codec.instant raw) "UTC microsecond form accepted"

    for raw in
        [
            "2026-10-05T03:00:00Z"
            "2026-10-05t03:00:00.000000z"
            "2026-10-05T03:00:00.0000000+00:00"
            "2026-10-05T03:00:00.000000+00:00"
            "2026-02-29T03:00:00.000000Z"
            "2026-10-05T25:00:00.000000Z"
        ] do
        Expect.isFalse (admits Codec.instant raw) "Other spellings and invalid instants refuse"

let private calendar () =
    for raw in [ "2024-02-29"; "0001-01-01"; "9999-12-31" ] do
        Expect.isTrue (admits ResponseScalars.date raw) "Permanent valid dates accepted"

    for raw in [ "0000-01-01"; "2026-02-29"; "2026-04-31"; "2026-10-05\n"; "2026-1-01" ] do
        Expect.isFalse
            (admits ResponseScalars.date raw)
            "Impossible dates and loose spellings refuse"

let private references () =
    for expected, values in
        [
            true, [ "Demo-1"; String.replicate 80 "A"; "!~<>\"" ]
            false, [ ""; "a b"; "é"; String.replicate 81 "A"; "a\u0000b"; "a\n" ]
        ] do
        for value in values do
            Expect.equal
                (admits ResponseScalars.reference value)
                expected
                "Accepted history has permanent reference grammar"

            Expect.equal
                (ClaimCore.Domain.CaseReference.validate value |> Result.isOk)
                expected
                "Permanent format matches Domain"

let private businessAdmission () =
    for reference in [ ""; "a b"; "é" ] do
        let raw = JsonSerializer.SerializeToUtf8Bytes {| caseReference = reference |}

        Expect.isOk
            (Admission.admit Endpoint.CaseGet raw)
            "Business format reaches typed Application refusal"

    for raw in [ "0"; "123456789012345678.1234" ] do
        Expect.isTrue (admits ResponseScalars.amount raw) "Permanent decimal grammar"

    for raw in [ "-1"; "01"; "1e2"; "1234567890123456789"; "1.12345" ] do
        Expect.isFalse (admits ResponseScalars.amount raw) "Amounts cannot leave durable grammar"

[<Tests>]
let tests =
    testList
        "wire scalar formats"
        [
            testCase "instants use exact UTC microsecond spelling" timestamps
            testCase "calendar dates exist and have exact spelling" calendar
            testCase "accepted references preserve printable ASCII identity" references
            testCase
                "business request admission and accepted response grammars are distinct"
                businessAdmission
        ]
