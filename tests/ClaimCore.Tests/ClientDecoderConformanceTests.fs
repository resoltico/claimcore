module ClaimCore.Tests.ClientDecoderConformanceTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.ClientProtocol
open ClaimCore.ClientContract

let private fixture name =
    let root = ClaimCore.TestSupport.RepositoryRoot.find ()
    JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, name)))

let private text (name: string) (sample: JsonElement) =
    sample.GetProperty(name).GetString() |> nonNull

let private payload (sample: JsonElement) =
    match sample.TryGetProperty "hex" with
    | true, value -> Convert.FromHexString(value.GetString() |> nonNull)
    | _ -> Encoding.UTF8.GetBytes(text "text" sample)

let private admits endpoint direction (bytes: byte array) =
    if direction = "response" then
        Frames.response (EndpointCatalog.tryFind endpoint |> Option.get) bytes
        |> Result.isOk
    else
        let prefix = Encoding.UTF8.GetBytes("{\"endpoint\":\"" + endpoint + "\",\"input\":")

        Array.concat [ prefix; bytes; Encoding.UTF8.GetBytes "}" ]
        |> Frames.parse
        |> Result.isOk

let private independentByteCases () =
    use document = fixture "tests/fixtures/client-byte-conformance.json"
    let cases = document.RootElement.GetProperty("cases").EnumerateArray() |> Seq.toList
    Expect.equal cases.Length 48 "Independent byte cases cannot disappear silently"

    for sample in cases do
        Expect.equal
            (admits (text "endpoint" sample) (text "direction" sample) (payload sample))
            (sample.GetProperty("valid").GetBoolean())
            (text "id" sample)

let private generatedEndpointCases () =
    use document = fixture "contracts/endpoint-corpus.json"
    let mutable positive, negative = 0, 0

    for sample in document.RootElement.GetProperty("cases").EnumerateArray() do
        let expected = sample.GetProperty("valid").GetBoolean()

        if expected then
            positive <- positive + 1
        else
            negative <- negative + 1

        let bytes = sample.GetProperty("value").GetRawText() |> Encoding.UTF8.GetBytes

        Expect.equal
            (admits (text "endpoint" sample) (text "direction" sample) bytes)
            expected
            (text "id" sample)

    Expect.isGreaterThan positive 0 "Generated production native catalogue accepts positive cases"
    Expect.isGreaterThan negative 0 "Generated production native catalogue rejects negative cases"

let private authoritativeIdentityAdmission () =
    use document = fixture "tests/fixtures/client-byte-conformance.json"
    let mutable count = 0

    for sample in document.RootElement.GetProperty("cases").EnumerateArray() do
        match sample.TryGetProperty "serverIdentityValid" with
        | true, expected ->
            count <- count + 1

            Expect.equal
                (ClaimCore.Contracts.EndpointCatalog.admitRequest
                    (text "endpoint" sample)
                    (payload sample)
                 |> Result.isOk)
                (expected.GetBoolean())
                ("Unchanged authoritative identity admission: " + text "id" sample)
        | _ -> ()

    Expect.equal count 22 "All independent principal cases reach authoritative typed binding"

let private uuidWithoutLength () =
    let document =
        {
            Identifier = "uuid-format"
            Title = "UUID format"
            Root = Schema.string (Some "uuid") None None None
            Definitions = []
        }

    let valid = "10000000-0000-4000-8000-00000000000a"

    for value, expected in
        [
            valid, true
            valid + "\n", false
            valid + "x", false
            valid.ToUpperInvariant(), false
            "00000000-0000-0000-0000-000000000000", false
        ] do
        let bytes = JsonSerializer.Serialize(value) |> Encoding.UTF8.GetBytes

        Expect.equal
            (HttpInputSupport.parse bytes (SchemaValueValidation.verify document)
             |> Result.defaultValue false)
            expected
            "Unbounded UUID format consumes exactly one canonical nonempty UUID"

[<Tests>]
let tests =
    testList
        "production client decoder conformance"
        [
            testCase "independent positive and negative raw bytes" independentByteCases
            testCase "all generated positive and negative endpoint bytes" generatedEndpointCases
            testCase
                "wire admission leaves authoritative identity policy unchanged"
                authoritativeIdentityAdmission
            testCase "UUID format without length requires exact end of input" uuidWithoutLength
        ]
