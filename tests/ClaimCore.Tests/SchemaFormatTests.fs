module ClaimCore.Tests.SchemaFormatTests

open ClaimCore.ClientProtocol

open System
open System.Text.Json
open Expecto
open ClaimCore.Contracts

let private verifies schema value =
    use document = JsonDocument.Parse(JsonSerializer.Serialize(value: string))

    SchemaValueValidation.verify
        {
            Identifier = "synthetic-format"
            Title = "Synthetic format boundary"
            Root = schema
            Definitions = []
        }
        document.RootElement

let private timestamp = Schema.string (Some "date-time") None None None

let private rejectsIncompleteTimestamps () =
    for value in
        [
            "10/5/2026"
            "2026-10-05"
            "2026-10-05T03:00:00"
            "Mon, 05 Oct 2026 03:00:00 GMT"
            " 2026-10-05T03:00:00.0000000+00:00 "
            "2026-10-05T03:00Z"
            "2026-02-29T03:00:00Z"
            "2026-10-05T25:00:00Z"
        ] do
        Expect.isFalse (verifies timestamp value) "Incomplete or invalid timestamp refused"

let private acceptsExplicitInstants () =
    for value in
        [
            "2026-10-05T03:00:00Z"
            "2026-10-05t03:00:00z"
            "2026-10-05T03:00:00.1+05:30"
            "2026-10-05T03:00:00.1234567-05:30"
            "2024-02-29T03:00:00.0000000+00:00"
        ] do
        Expect.isTrue (verifies timestamp value) "Representable explicit RFC 3339 instant accepted"

let private canonicalWireRestrictionRemains () =
    let canonical =
        Schema.string
            (Some "date-time")
            (Some "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{7}\\+00:00$")
            (Some 33)
            (Some 33)

    Expect.isTrue
        (verifies canonical "2026-10-05T03:00:00.0000000+00:00")
        "Canonical UTC timestamp accepted"

    Expect.isFalse
        (verifies canonical "2026-10-05T03:00:00Z")
        "A format-valid value cannot bypass stricter schema constraints"

let private referenceParity () =
    for expected, values in
        [
            true, [ "Demo-1"; String.replicate 80 "A"; "!~<>\"" ]
            false, [ ""; "a b"; "é"; String.replicate 81 "A"; "a\u0000b"; "a\n" ]
        ] do
        for value in values do
            let json =
                JsonSerializer.Serialize {| caseReference = value |}
                |> System.Text.Encoding.UTF8.GetBytes

            Expect.equal
                (EndpointCatalog.admitRequest "case.get" json |> Result.isOk)
                expected
                "Wire format"

            Expect.equal
                (ClaimCore.Domain.CaseReference.validate value |> Result.isOk)
                expected
                "Domain format"

[<Tests>]
let tests =
    testList
        "schema scalar formats"
        [
            testCase
                "[CC-DOM-001] projected reference guard preserves printable ASCII identity"
                referenceParity
            testCase
                "date-time refuses implicit calendar and local-zone inputs"
                rejectsIncompleteTimestamps
            testCase
                "date-time accepts explicit representable RFC 3339 instants"
                acceptsExplicitInstants
            testCase
                "format validation preserves canonical wire restrictions"
                canonicalWireRestrictionRemains
        ]
