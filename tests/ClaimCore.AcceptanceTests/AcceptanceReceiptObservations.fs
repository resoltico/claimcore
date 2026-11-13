module internal ClaimCore.AcceptanceTests.ReceiptObservations

open System
open System.IO
open System.Globalization
open System.Text
open System.Text.Json
open Expecto
open CliCalls

let private observedInstant text =
    DateTimeOffset.ParseExact(
        text,
        "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
    )

let private observation observed businessDate zone =
    Expect.equal zone "Etc/UTC" "The owned fixture uses the installation zone"
    let instant = observedInstant observed

    Expect.equal
        businessDate
        (instant.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        "UTC fixture business date belongs to its actual snapshot instant"

    instant

let private observedBasis (basis: JsonElement) =
    let names = basis.EnumerateObject() |> Seq.map _.Name |> Seq.sort |> Seq.toList

    Expect.equal
        names
        [ "businessDate"; "observedAt"; "timeZone" ]
        "Current basis has exactly its declared fields"

    observation
        (basis.GetProperty("observedAt").GetString() |> nonNull)
        (basis.GetProperty("businessDate").GetString() |> nonNull)
        (basis.GetProperty("timeZone").GetString() |> nonNull)

let private currentObservation fixture =
    let context = automation fixture "context" {| |} 0 |> require "SUCCEEDED"
    let requester = context.GetProperty "requester"

    Expect.equal
        (requester.GetProperty("kind").GetString())
        "ACTIVE"
        "Receipt bounds use the registered caller's own context"

    observation
        (requester.GetProperty("observedAt").GetString() |> nonNull)
        (requester.GetProperty("businessDate").GetString() |> nonNull)
        (requester.GetProperty("timeZone").GetString() |> nonNull)

let private recordedReceipt (receipt: JsonElement) =
    use output = new MemoryStream()
    use writer = new Utf8JsonWriter(output)
    writer.WriteStartObject()

    for field in receipt.EnumerateObject() do
        if field.Name <> "basis" then
            writer.WritePropertyName field.Name
            writer.WriteRawValue(field.Value.GetRawText())

    writer.WriteEndObject()
    writer.Flush()
    Encoding.UTF8.GetString(output.ToArray())

/// Only the explicitly current transaction basis varies; every other receipt member stays exact.
let sameRecordedReceipt (fixture: PublishedFixture.Fixture) name (expected: JsonElement) read =
    let before = currentObservation fixture
    let actual: JsonElement = read ()
    let after = currentObservation fixture
    let priorBasis = observedBasis (expected.GetProperty "basis")
    let actualBasis = observedBasis (actual.GetProperty "basis")

    Expect.isGreaterThanOrEqual
        priorBasis
        (observedInstant (expected.GetProperty("recordedAt").GetString() |> nonNull))
        "Initial receipt observation does not predate its event"

    Expect.isLessThanOrEqual priorBasis before "Initial receipt predates the later own snapshot"
    Expect.isGreaterThanOrEqual actualBasis before "Replay observation is not an earlier snapshot"

    Expect.isLessThanOrEqual
        actualBasis
        after
        "Replay observation is bounded by the next authorized snapshot"

    let previous = recordedReceipt expected
    let replay = recordedReceipt actual

    if replay <> previous then
        FixtureFiles.text fixture.Directory (name + ".expected.json") (expected.GetRawText())
        |> ignore

        FixtureFiles.text fixture.Directory (name + ".actual.json") (actual.GetRawText())
        |> ignore

    Expect.equal
        replay
        previous
        "Every remaining receipt member (including actor refs, snapshots, fields, revisions, effects, positions and RecordedAt) remains exact"

    actual

let contentGuards =
    testCase "receipt comparison preserves every non-basis member and raw nested value" (fun () ->
        let receipt =
            """{"basis":{"observedAt":"current-one"},"operationId":"stable-id","position":{"sequence":7,"entryHash":"hash"},"recordedAt":"event-time","recordedBy":{"actorId":"recorder","displayName":{"kind":"NAMED","value":"Current recorder"}},"subject":{"revision":3,"effects":[{"act":"people.setProfile"}],"snapshot":{"fields":{"amount":"1.2345","text":"literal\\nparagraph"}}}}"""

        let event text =
            use document = JsonDocument.Parse(text: string)
            recordedReceipt document.RootElement

        let expected = event receipt

        Expect.equal
            (event (receipt.Replace("current-one", "current-two")))
            expected
            "Only the declared current basis is compared separately"

        for before, after in
            [
                "stable-id", "foreign-id"
                "event-time", "other-event-time"
                "\"sequence\":7", "\"sequence\":8"
                "\"entryHash\":\"hash\"", "\"entryHash\":\"tampered\""
                "\"actorId\":\"recorder\"", "\"actorId\":\"other-recorder\""
                "Current recorder", "Different current recorder"
                "\"revision\":3", "\"revision\":4"
                "SET_PROFILE", "ERASE_PROFILE"
                "1.2345", "1.2340"
                "literal\\\\nparagraph", "literal\\nparagraph"
                "\"operationId\":", "\"unexpected\":\"must remain visible\",\"operationId\":"
            ] do
            Expect.notEqual
                (event (receipt.Replace(before, after)))
                expected
                "No other member, actor reference, date, effect, amount, text encoding or unknown field can disappear")
