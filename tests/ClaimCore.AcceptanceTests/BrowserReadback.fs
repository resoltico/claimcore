module internal ClaimCore.AcceptanceTests.BrowserReadback

open System
open System.IO
open System.Text.Json
open Expecto
open CliCalls

let private verifyReceipt reference operationId (current: JsonElement) (receipt: JsonElement) =
    let scope = receipt.GetProperty("scope")
    Expect.equal (scope.GetProperty("kind").GetString()) "CASE" "Browser case receipt"
    Expect.equal (scope.GetProperty("act").GetString()) "OPEN" "Browser opening act"
    Expect.equal (scope.GetProperty("revision").GetInt64()) 1L "One browser acceptance"

    Expect.isTrue
        (scope.GetProperty("caseReference").GetString() = reference)
        "Exact browser reference"

    Expect.isTrue (receipt.GetProperty("operationId").GetString() = operationId) "Browser operation"

    let snapshot = scope.GetProperty("snapshot")

    Expect.equal
        (snapshot.GetProperty("disclosure").GetString())
        "DISCLOSED"
        "Authorized browser snapshot"

    Expect.isTrue
        (JsonElement.DeepEquals(snapshot.GetProperty("view"), current.GetProperty("state")))
        "Same complete snapshot state"

let qualify fixture output =
    use document =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "unicode-case.json")))

    let authored = document.RootElement
    let reference = authored.GetProperty("reference").GetString() |> nonNull
    let operationId = authored.GetProperty("operationId").GetString() |> nonNull

    let current =
        automation fixture "case.get" {| caseReference = reference |} 0
        |> require "SUCCEEDED"

    Expect.equal (current.GetProperty("revision").GetInt64()) 1L "One browser acceptance"

    let registration =
        current.GetProperty("state").GetProperty("fields").GetProperty("registration")

    for field, expected in
        [
            "claimantName", String.replicate 200 "🙂"
            "insurerName", String.replicate 200 "🙂"
            "incidentCountry", "LT"
        ] do
        Expect.isTrue
            (registration.GetProperty(field).GetString() = expected)
            "Exact native Unicode readback"

    let receipt =
        automation fixture "operation.observe" {| operationId = operationId |} 0
        |> require "SUCCEEDED"

    verifyReceipt reference operationId current receipt
