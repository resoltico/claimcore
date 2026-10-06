module ClaimCore.Tests.PresentationProjectionTests

open System
open System.Text.Json
open Expecto
open ClaimCore.ContractGeneration

let private literalProjection () =
    let alphabet = [ "{"; "}"; "'"; "a" ]

    let rec words length =
        if length = 0 then
            [ "" ]
        else
            [
                for prefix in words (length - 1) do
                    for suffix in alphabet do
                        prefix + suffix
            ]

    let values =
        [
            yield!
                [
                    ""
                    "Handler's claim"
                    "Use {literal} and {}"
                    "{'{"
                    "{'}"
                    "}'{"
                    "}'}"
                    "é العربية 😀"
                ]
            for length in 1..6 do
                yield! words length
        ]

    for value in values do
        let projection = [ DefaultPresentation.literal value ]
        let bytes = JsonSerializer.SerializeToUtf8Bytes(projection)
        use parsed = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
        let literal = parsed.RootElement.[0].GetProperty("literal").GetString()
        Expect.equal literal value "Actual serialized F# projection retains literal text"

let private diagnosticProjection () =
    use document = JsonDocument.Parse(DefaultPresentation.bytes ())

    for entry in document.RootElement.EnumerateObject() do
        Expect.equal
            entry.Value.ValueKind
            JsonValueKind.Array
            "Generated projection uses typed parts"

        for part in entry.Value.EnumerateArray() do
            let properties = part.EnumerateObject() |> Seq.toList
            Expect.equal properties.Length 1 "Each part has one tag"

            Expect.isTrue
                (List.contains properties.Head.Name [ "literal"; "hole" ])
                "Only declared part tags"

let tests =
    testList
        "build-only presentation projection"
        [
            testCase
                "exports serialized lossless literals across syntax interactions [CC-WEB-001]"
                literalProjection
            testCase
                "exports every diagnostic as literal and declared hole parts [CC-WEB-001]"
                diagnosticProjection
        ]
