module internal ClaimCore.AcceptanceTests.BrowserReadback

open System
open System.IO
open System.Text.Json
open Expecto
open CliCalls

let private verifyReceipt reference operationId (current: JsonElement) (receipt: JsonElement) =
    let subject = receipt.GetProperty("subject")
    Expect.equal (subject.GetProperty("kind").GetString()) "CASE" "Browser case receipt"
    Expect.equal (subject.GetProperty("act").GetString()) "OPEN" "Browser opening act"
    Expect.equal (subject.GetProperty("revision").GetInt64()) 1L "One browser acceptance"

    Expect.isTrue
        (subject.GetProperty("caseReference").GetString() = reference)
        "Exact browser reference"

    Expect.isTrue (receipt.GetProperty("operationId").GetString() = operationId) "Browser operation"

    let snapshot = subject.GetProperty("snapshot")

    Expect.equal
        (snapshot.GetProperty("kind").GetString())
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
        automation
            fixture
            "case.get"
            {|
                target =
                    {|
                        kind = "REFERENCE"
                        caseReference = reference
                    |}
            |}
            0
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
