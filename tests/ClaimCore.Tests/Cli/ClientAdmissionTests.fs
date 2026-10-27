module ClaimCore.Tests.ClientAdmissionTests

open ClaimCore.Cli

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Contract.Protocol

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
        StrictJson.read Limits.MaximumResponseBytes bytes
        |> Result.bind (Api.admitOutput (Api.tryFind endpoint |> Option.get))
        |> Result.isOk
    else
        let prefix = Encoding.UTF8.GetBytes("{\"endpoint\":\"" + endpoint + "\",\"input\":")

        Array.concat [ prefix; bytes; Encoding.UTF8.GetBytes "}" ]
        |> ClaimCore.Cli.Frames.admit
        |> Result.isOk

let private independentByteCases () =
    use document = fixture "tests/fixtures/client-byte-conformance.json"
    let cases = document.RootElement.GetProperty("cases").EnumerateArray() |> Seq.toList
    Expect.equal cases.Length 50 "Independent byte cases cannot disappear silently"

    for sample in cases do
        Expect.equal
            (admits (text "endpoint" sample) (text "direction" sample) (payload sample))
            (sample.GetProperty("valid").GetBoolean())
            (text "id" sample)

let private authoritativeIdentityAdmission () =
    use document = fixture "tests/fixtures/client-byte-conformance.json"
    let mutable count = 0

    for sample in document.RootElement.GetProperty("cases").EnumerateArray() do
        match sample.TryGetProperty "serverIdentityValid" with
        | true, expected ->
            count <- count + 1

            Expect.equal
                (Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input (payload sample)
                 |> Result.mapError _.Reason
                 |> Result.bind (function
                     | OperationInput.Authority request ->
                         ClaimCore.Domain.Authority.parse request
                         |> Result.mapError (fun _ -> "invalid principal")
                     | _ -> Error "Expected authority request")
                 |> Result.isOk)
                (expected.GetBoolean())
                ("Unchanged authoritative identity admission: " + text "id" sample)
        | _ -> ()

    Expect.equal count 22 "All independent principal cases reach authoritative typed binding"

let private exactUuid () =
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
            (Codec.decode 100 Codec.uuid bytes |> Result.isOk)
            expected
            "Unbounded UUID format consumes exactly one canonical nonempty UUID"

[<Tests>]
let tests =
    testList
        "production client decoder conformance"
        [
            testCase "independent positive and negative raw bytes" independentByteCases
            testCase
                "wire admission leaves authoritative identity policy unchanged"
                authoritativeIdentityAdmission
            testCase "UUID codec requires exact canonical input" exactUuid
        ]
