module ClaimCore.Tests.Utf8ByteLimitTests

open System
open System.Text
open System.Text.Json
open Expecto
open global.Json.Schema
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

let private witnesses =
    [
        "ASCII1024", String('x', 1024), true
        "ASCII1025", String('x', 1025), false
        "two-byte boundary", String.replicate 512 "é", true
        "two-byte over", String.replicate 513 "é", false
        "three-byte mixed boundary", String.replicate 341 "€" + "x", true
        "three-byte mixed over", String.replicate 341 "€" + "xy", false
        "supplementary boundary", String.replicate 256 "😀", true
        "supplementary over", String.replicate 256 "😀" + "x", false
        "joining boundary", String.replicate 93 "👩‍💻" + "x", true
        "joining over", String.replicate 93 "👩‍💻" + "xy", false
    ]

[<Tests>]
let tests =
    testList
        "declared UTF-8 string byte limits"
        [
            testCase
                "native generic codec and independent schema assert the same byte boundaries"
                (fun () ->
                    let codec = Codec.boundedUtf8Text 1 1024

                    let schema =
                        Utf8SchemaValidation.build
                            """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"string","minLength":1,"maxLength":1024,"x-claimcore-maxUtf8Bytes":1024}"""

                    for label, text, expected in witnesses do
                        let bytes = JsonSerializer.Serialize text |> Encoding.UTF8.GetBytes
                        use value = JsonDocument.Parse(bytes.AsMemory())
                        Expect.equal (Codec.decode 8192 codec bytes |> Result.isOk) expected label

                        Expect.equal
                            (Utf8SchemaValidation.evaluate schema value.RootElement)
                            expected
                            (label + " independent engine")

                        Expect.equal
                            (PartyName.parse text |> Result.isOk)
                            expected
                            (label + " unchanged permanent Domain bound"))
            testCase
                "vanilla JSON Schema alone cannot assert the vendor wire-profile keyword"
                (fun () ->
                    let raw =
                        """{"type":"string","maxLength":1024,"x-claimcore-maxUtf8Bytes":1024}"""

                    let vanilla =
                        JsonSchema.FromText(raw, BuildOptions(Dialect = Dialect.Draft202012))

                    let extended = Utf8SchemaValidation.build raw

                    use value =
                        JsonDocument.Parse(JsonSerializer.Serialize(String.replicate 513 "é"))

                    Expect.isTrue
                        (vanilla.Evaluate(value.RootElement).IsValid)
                        "Unaware validators treat the extension as an annotation"

                    Expect.isFalse
                        (Utf8SchemaValidation.evaluate extended value.RootElement)
                        "The required profile adapter rejects the same multibyte overflow")
            testCase
                "publication carries the byte fact while authored admission remains separate"
                (fun () ->
                    let schema =
                        Codec.boundedUtf8Text 1 1024
                        |> fun value -> SchemaJson.value "" value.Schema

                    Expect.equal
                        (JsonValue.tryMember "x-claimcore-maxUtf8Bytes" schema)
                        (Some(JsonValue.Integer 1024L))
                        "The byte limit is a published structural fact"

                    let permanent =
                        PartyName.parse (String('x', 1024))
                        |> Result.defaultWith (string >> failtest)

                    Expect.isError
                        (PartyName.admit permanent)
                        "Existing authored200-scalar policy is not replaced by wire byte representability"

                    Expect.isOk
                        (PartyName.parse (String.replicate 200 "😀") |> Result.bind PartyName.admit)
                        "Current valid supplementary names stay admitted"

                    let authored: AuthorityRequest =
                        {
                            OperationId = Guid.Parse "65f2c361-c10f-7bd9-bacf-54ce57716701"
                            Target =
                                {
                                    Kind = PrincipalKind.Human
                                    Issuer = "https://bytes.example/"
                                    Subject = "new-profile"
                                }
                            Act =
                                AuthorityAct.Register
                                    {
                                        DisplayName = String('x', 1025)
                                        Contact = None
                                    }
                        }

                    let raw = ClaimCore.Application.Contracts.OperationInput.Authority authored
                    let bytes = Codec.encode Api.operationExecute.Input raw

                    Expect.isOk
                        (Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input bytes)
                        "Raw authored strings still reach Application rather than read-side wire constraints"

                    match Authority.parse authored with
                    | Error(Refusal.InvalidInput issues) ->
                        Expect.equal
                            (InputIssues.toList issues
                             |> List.map (fun issue -> issue.Target, issue.Violation))
                            [ InputTarget.ProfileDisplayName, Violation.TooLong 200 ]
                            "Unchanged current authored rejection"
                    | other ->
                        failtestf "Expected the original authored admission boundary: %A" other

                    Expect.equal PartyName.MaximumBytes 1024 "No permanent bound raised"
                    Expect.equal Limits.MaximumRequestBytes 65536 "No request bound raised"
                    Expect.equal Limits.MaximumResponseBytes 4194304 "No reply bound raised")
        ]
