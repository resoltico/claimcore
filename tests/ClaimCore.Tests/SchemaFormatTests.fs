module ClaimCore.Tests.SchemaFormatTests

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

let private requiredText (value: JsonElement) =
    value.GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "The projected scalar corpus requires text.")

let private nativeScalarCorpus () =
    let model = ContractProjection.current ()
    let _, bytes = ClaimCore.ContractGeneration.WebParsedCorpus.artifact model
    use corpus = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
    let mutable positiveIds = Set.empty
    let mutable negativeCount = 0

    for sample in corpus.RootElement.GetProperty("cases").EnumerateArray() do
        let id = requiredText (sample.GetProperty("id"))

        if
            id.StartsWith("scalar-", StringComparison.Ordinal)
            || id = "valid-case-get-found-scalar-boundaries"
            || id = "valid-case-get-found-four-byte-boundary"
            || id = "valid-case-get-found-scalar-one"
        then
            let endpoint =
                model.WebEndpoints
                |> List.find (fun value ->
                    value.Identifier = requiredText (sample.GetProperty("endpoint")))

            let actual =
                SchemaValueValidation.verify
                    (WebSchemas.responseDocument model endpoint)
                    (sample.GetProperty("value"))

            Expect.equal actual (sample.GetProperty("valid").GetBoolean()) id

            if sample.GetProperty("valid").GetBoolean() then
                positiveIds <- Set.add id positiveIds
            else
                negativeCount <- negativeCount + 1

    Expect.equal
        positiveIds
        (set
            [
                "valid-case-get-found-scalar-boundaries"
                "valid-case-get-found-four-byte-boundary"
                "valid-case-get-found-scalar-one"
            ])
        "All supplementary positive controls ran"

    Expect.isGreaterThan negativeCount 0 "Negative scalar corpus members ran"

let private referenceUnicodeParity () =
    let contract =
        CliSchemas.endpoint (ContractProjection.current ()) "case.get"
        |> Option.defaultWith (fun () -> failtest "Projected case input is required.")
        |> CanonicalContract.text

    use schema = JsonDocument.Parse(contract)

    let resolve (value: JsonElement) =
        match value.TryGetProperty("$ref") with
        | true, reference ->
            let name = requiredText reference
            schema.RootElement.GetProperty("$defs").GetProperty(name.Substring("#/$defs/".Length))
        | _ -> value

    let constraints =
        (resolve schema.RootElement).GetProperty("properties").GetProperty("caseReference")
        |> resolve

    let pattern = requiredText (constraints.GetProperty("pattern"))
    let maximum = constraints.GetProperty("maxLength").GetInt32()
    let minimum = constraints.GetProperty("minLength").GetInt32()

    let high = string (char 0xd800)
    let low = string (char 0xdc00)

    let accepted =
        [ "🙂"; String.replicate 80 "🙂"; "a\u200db"; "a\u202eb"; "\ufeffa"; "e\u0301" ]

    let refused =
        [
            high
            low
            low + high
            high + high
            high + "a" + low
            String.replicate 81 "🙂"
            "\u0085a"
            "a\u3000"
            "a\u0000b"
            ""
        ]

    for expected, values in [ true, accepted; false, refused ] do
        for value in values do
            let native =
                System.Text.RegularExpressions.Regex.IsMatch(value, pattern)
                && (value.EnumerateRunes() |> Seq.length) <= maximum
                && (value.EnumerateRunes() |> Seq.length) >= minimum

            Expect.equal native expected "Independent Unicode/length oracle"

            Expect.equal
                (ClaimCore.Domain.Claim.validateReference value |> Result.isOk)
                expected
                "Domain accepts the same exact text"

let tests =
    testList
        "schema scalar formats"
        [
            testCase
                "[CC-DOM-001] native evaluator admits the shared scalar corpus"
                nativeScalarCorpus
            testCase
                "[CC-DOM-001] projected reference guard preserves Unicode scalar semantics"
                referenceUnicodeParity
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
