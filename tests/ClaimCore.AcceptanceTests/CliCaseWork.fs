module internal ClaimCore.AcceptanceTests.CliCaseWork

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open CliCalls

let qualify (fixture: PublishedFixture.Fixture) =
    let environment = interactiveEnvironment fixture "synthetic-owner" "valid"
    let reference = "CLI-WORK-" + id ()

    let context =
        human fixture "synthetic-owner" "context" {| |} 0 |> require "SUCCEEDED"

    let actor = context.GetProperty("actorId").GetString() |> nonNull

    let fact act revision flags =
        verb
            fixture
            environment
            ([
                "case"
                act
                reference
                "--operation-id"
                id ()
                "--expected-revision"
                string revision
             ]
             @ flags)
            0
        |> require "ACCEPTED"

    let work revision flags =
        verb
            fixture
            environment
            ([
                "work"
                "update"
                reference
                "--operation-id"
                id ()
                "--expected-work-revision"
                string revision
             ]
             @ flags)
            0
        |> require "ACCEPTED"

    let reminder act flags =
        verb fixture environment ([ "reminder"; act; reference; "--operation-id"; id () ] @ flags) 0
        |> require "ACCEPTED"

    let current () =
        verb fixture environment [ "case"; "get"; reference ] 0 |> require "SUCCEEDED"

    let expected reminder revision =
        [ "--reminder-id"; reminder; "--expected-reminder-revision"; string revision ]

    let reminderId (receipt: JsonElement) =
        receipt
            .GetProperty("subject")
            .GetProperty("effects")
            .GetProperty("reminder")
            .GetProperty("reminderId")
            .GetString()
        |> nonNull

    let source = "  Incident context\r\nالعربية 😀  \r\n"
    let path = Path.Combine(fixture.Directory, "incident-" + id () + ".txt")
    File.WriteAllText(path, source, UTF8Encoding(false))

    let opened =
        verb
            fixture
            environment
            [
                "case"
                "open"
                reference
                "--operation-id"
                id ()
                "--incident-date"
                "2026-09-01"
                "--incident-notification-date"
                "2026-09-02"
                "--incident-country"
                "LV"
                "--claimant-name"
                "CLI work claimant"
                "--insurer-name"
                "CLI work insurer"
                "--claimed-amount"
                "100"
                "--claimed-currency"
                "EUR"
                "--incident-description-file"
                path
            ]
            0
        |> require "ACCEPTED"

    Expect.equal
        (opened.GetProperty("subject").GetProperty("revision").GetInt64())
        1L
        "OPEN accepts one fact version"

    Expect.equal (File.ReadAllText path) source "The caller's CRLF file is preserved"
    let initial = current ()

    Expect.equal
        (initial.GetProperty("work").GetProperty("revision").GetInt64())
        0L
        "OPEN establishes the empty independent work root"

    Expect.isTrue
        (initial.GetProperty("work").GetProperty("needsNextAction").GetBoolean())
        "Opening without a plan is accepted and needs attention"

    Expect.equal
        (initial
            .GetProperty("state")
            .GetProperty("fields")
            .GetProperty("registration")
            .GetProperty("incidentDescription")
            .GetString())
        (source.Replace("\r\n", "\n"))
        "The prepared LF narrative survives actual service and CLI serialization"

    work
        0
        [
            "--responsible-actor-action"
            "set"
            "--responsible-actor"
            actor
            "--handling-summary"
            "Current handling\nSecond paragraph"
        ]
    |> ignore

    let first =
        reminder "create" [ "--action"; "Contact insurer"; "--due"; "2027-12-31" ]
        |> reminderId

    let second =
        reminder "create" [ "--action"; "=2+3"; "--due"; "2027-12-30" ] |> reminderId

    let queue =
        verb
            fixture
            environment
            [ "reminder"; "list"; "--case-reference"; reference; "--limit"; "50" ]
            0
        |> require "SUCCEEDED"

    Expect.equal
        (queue.GetProperty("items").GetArrayLength())
        2
        "Both independently identified pending reminders are visible"

    Expect.isTrue
        (queue.GetProperty("items").EnumerateArray()
         |> Seq.exists (fun row ->
             row.GetProperty("reminder").GetProperty("action").GetString() = "=2+3"))
        "JSON preserves formula-looking action text exactly"

    let csv =
        invoke
            fixture
            environment
            [
                "reminder"
                "list"
                "--case-reference"
                reference
                "--limit"
                "50"
                "--format"
                "csv"
            ]
            Array.empty
            0

    Expect.equal csv.ExitCode 0 "Actual reminder CSV command succeeds"

    Expect.isTrue
        (Encoding.UTF8.GetString(csv.StandardOutput).Contains("'=2+3", StringComparison.Ordinal))
        "CSV neutralizes the formula without rewriting JSON or stored text"

    let compound =
        reminder
            "complete-and-plan-next"
            (expected first 1 @ [ "--action"; "Review response"; "--due"; "2027-12-31" ])

    let effects = compound.GetProperty("subject").GetProperty("effects")

    Expect.equal
        (effects.GetProperty("kind").GetString())
        "COMPLETED_AND_PLANNED"
        "Published CLI executes the one-operation bounded pair"

    let next =
        effects.GetProperty("next").GetProperty("reminderId").GetString() |> nonNull

    Expect.equal
        next
        (compound.GetProperty("operationId").GetString() |> nonNull)
        "Replacement identity equals the accepted operation identity"

    reminder "cancel" (expected second 1) |> ignore
    reminder "reopen" (expected second 2 @ [ "--due"; "2027-12-30" ]) |> ignore

    reminder
        "update"
        (expected second 3 @ [ "--action"; "Review settlement"; "--due"; "2027-12-29" ])
    |> ignore

    reminder "complete" (expected second 4) |> ignore

    let terminal =
        verb fixture environment [ "reminder"; "get"; reference; "--reminder-id"; second ] 0
        |> require "SUCCEEDED"

    Expect.equal
        (terminal.GetProperty("item").GetProperty("reminder").GetProperty("state").GetString())
        "COMPLETED"
        "ReadCase permits current terminal records without historical payload access"

    let last =
        invoke
            fixture
            environment
            ([
                "reminder"
                "complete"
                reference
                "--operation-id"
                id ()
                "--format"
                "text"
             ]
             @ expected next 1)
            Array.empty
            0

    Expect.equal last.ExitCode 0 "The last truthful completion is accepted"

    Expect.isTrue
        (Encoding.UTF8
            .GetString(last.StandardOutput)
            .Contains("This actionable case has no pending next action.", StringComparison.Ordinal))
        "Human output refreshes core coverage after last completion"

    let afterWork = current ()

    Expect.equal
        (afterWork.GetProperty("revision").GetInt64())
        1L
        "All reminder edits preserve financial fact revision"

    Expect.equal
        (afterWork.GetProperty("work").GetProperty("revision").GetInt64())
        1L
        "Reminder edits preserve work revision"

    Expect.equal
        (afterWork.GetProperty("work").GetProperty("pendingReminders").GetInt64())
        0L
        "Terminal reminders do not provide coverage"

    fact "amend-registration" 1 [ "--claimant-name"; "CLI selected fact" ] |> ignore

    fact
        "decide"
        2
        [
            "--payment-decision-date"
            "2026-09-03"
            "--payable-amount"
            "20"
            "--payable-currency"
            "EUR"
        ]
    |> ignore

    fact "record-payment" 3 [ "--payment-date"; "2026-09-04" ] |> ignore

    fact
        "correct"
        4
        [
            "--registration"
            "keep"
            "--decision"
            "update"
            "--payable-amount"
            "21"
            "--payable-currency"
            "EUR"
            "--payment"
            "reaffirm"
        ]
    |> ignore

    let paidDescription = "Paid context\nReconciled 😀"

    fact
        "correct"
        5
        [
            "--registration"
            "update"
            "--incident-description"
            paidDescription
            "--decision"
            "keep"
            "--payment"
            "keep"
        ]
    |> ignore

    let paid = current ()
    let fields = paid.GetProperty("state").GetProperty("fields")

    Expect.equal
        (paid.GetProperty("revision").GetInt64())
        6L
        "Only the five financial/narrative edits advance fact revision"

    Expect.equal
        (fields.GetProperty("registration").GetProperty("claimantName").GetString())
        "CLI selected fact"
        "Selected fact changes"

    Expect.equal
        (fields.GetProperty("registration").GetProperty("insurerName").GetString())
        "CLI work insurer"
        "Omitted fact survives"

    Expect.equal
        (fields.GetProperty("payment").GetProperty("paymentDate").GetString())
        "2026-09-04"
        "Description-only paid correction does not require or alter payment acknowledgement"

    Expect.equal
        (fields.GetProperty("decision").GetProperty("payableAmount").GetString())
        "21"
        "Complete money assertion is preserved"

    Expect.equal
        (fields.GetProperty("registration").GetProperty("incidentDescription").GetString())
        paidDescription
        "Paid description correction preserves exact paragraphs"

    let get =
        frame
            "case.get"
            {|
                target =
                    {|
                        kind = "REFERENCE"
                        caseReference = reference
                    |}
            |}

    let ndjson =
        invoke
            fixture
            environment
            [ "session" ]
            (Array.concat [ get; [| 10uy |]; get; [| 10uy |] ])
            0

    Expect.equal ndjson.ExitCode 0 "Actual NDJSON session succeeds"

    let rows =
        Encoding.UTF8
            .GetString(ndjson.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)

    Expect.hasLength rows 2 "Attention information never corrupts machine output"

    for row in rows do
        use document = JsonDocument.Parse row

        Expect.equal
            (document.RootElement
                .GetProperty("result")
                .GetProperty("outcome")
                .GetProperty("data")
                .GetProperty("state")
                .GetProperty("fields")
                .GetProperty("registration")
                .GetProperty("incidentDescription")
                .GetString())
            paidDescription
            "Each machine frame decodes the exact LF value"

    reminder "reopen" (expected first 2 @ [ "--due"; "2027-12-31" ]) |> ignore
    fact "close" 6 [] |> ignore
    work 1 [ "--handling-summary"; "Closed handling context" ] |> ignore
    reminder "complete" (expected first 3) |> ignore
    fact "reopen" 7 [] |> ignore
    let final = current ()

    Expect.equal
        (final.GetProperty("work").GetProperty("revision").GetInt64())
        2L
        "Closed-case summary edit owns its work revision"

    Expect.equal
        (final.GetProperty("work").GetProperty("summary").GetString())
        "Closed handling context"
        "Financial lifecycle edits never overwrite work"

    Expect.isTrue
        (final.GetProperty("work").GetProperty("needsNextAction").GetBoolean())
        "Reopening reevaluates missing-plan attention without a fake reminder"

    Expect.isTrue
        (final.GetProperty("mine").GetBoolean())
        "Responsibility remains factual and current"
