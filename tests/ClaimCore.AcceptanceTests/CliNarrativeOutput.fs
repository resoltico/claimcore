module internal ClaimCore.AcceptanceTests.CliNarrativeOutput

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open CliCalls

let qualify (fixture: PublishedFixture.Fixture) source reference (readHead: unit -> int64) =
    let owner = interactiveEnvironment fixture "synthetic-owner" "valid"
    CliCalls.run fixture fixture.Environment source 0 |> ignore

    CliCalls.verb
        fixture
        owner
        [
            "work"
            "update"
            reference
            "--expected-work-revision"
            "0"
            "--handling-summary"
            "Current handling context"
        ]
        0
    |> require "ACCEPTED"
    |> ignore

    let planned =
        CliCalls.verb
            fixture
            owner
            [
                "reminder"
                "create"
                reference
                "--action"
                "Contact insurer"
                "--due"
                "2027-12-31"
            ]
            0
        |> require "ACCEPTED"

    let reminder =
        planned
            .GetProperty("subject")
            .GetProperty("effects")
            .GetProperty("reminder")
            .GetProperty("reminderId")
            .GetString()
        |> nonNull

    let unsafe = "\u001B[31m\u0085\u202Eالعربية\u200C\u200D-literal\\u{001B} "

    let description =
        "RETAINED-DESCRIPTION-FIRST\n\nRETAINED-DESCRIPTION-SECOND"
        + unsafe
        + "END-DESCRIPTION"

    let summary =
        "RETAINED-SUMMARY-FIRST\n\nRETAINED-SUMMARY-SECOND" + unsafe + "END-SUMMARY"

    let action = "RETAINED-ACTION" + unsafe + "END-ACTION"

    let rewriteCase bytes =
        let root = JsonNode.Parse(bytes: byte array) |> nonNull
        let data = (root["outcome"] |> nonNull)["data"] |> nonNull

        (((data["state"] |> nonNull)["fields"] |> nonNull)["registration"] |> nonNull)["incidentDescription"] <-
            JsonValue.Create description

        (data["work"] |> nonNull)["summary"] <- JsonValue.Create summary
        Encoding.UTF8.GetBytes(root.ToJsonString())

    let rewriteReminder bytes =
        let root = JsonNode.Parse(bytes: byte array) |> nonNull

        ((((root["outcome"] |> nonNull)["data"] |> nonNull)["item"] |> nonNull)["reminder"]
         |> nonNull)["action"] <- JsonValue.Create action

        Encoding.UTF8.GetBytes(root.ToJsonString())

    let before = readHead ()
    fixture.TransformNextReply("/api/case.get", rewriteCase)
    fixture.TransformNextReply("/api/reminder.get", rewriteReminder)

    let start =
        ProcessStartInfo(
            "python3",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    for argument in
        [
            Path.Combine(fixture.Root, "tests/ClaimCore.AcceptanceTests/NarrativeTerminalProbe.py")
            fixture.Cli
            reference
            reminder
        ] do
        start.ArgumentList.Add argument

    for pair in owner do
        start.Environment[pair.Key] <- pair.Value

    start.Environment["PYTHONDONTWRITEBYTECODE"] <- "1"

    start.Environment["NARRATIVE_PROBE_PRIVATE_OUTPUT"] <-
        Path.Combine(fixture.Directory, "narrative-terminal.private-output")

    let progress = Some owner["CLAIMCORE_CLI_TEST_PROGRESS_FILE"]

    let terminal =
        CliDriverCompletion.run 90000 Console.Error progress 0 (fun () ->
            let result = ClaimCore.TestSupport.BoundedProcess.run start None 1048576 90000

            {
                ProcessRunner.Result.ExitCode = result.ExitCode
                StandardOutput = result.StandardOutput
                StandardError = result.StandardError
            })

    FixtureFiles.write fixture.Directory "narrative-terminal.stdout" terminal.StandardOutput
    |> ignore

    FixtureFiles.write fixture.Directory "narrative-terminal.stderr" terminal.StandardError
    |> ignore

    Expect.equal
        terminal.ExitCode
        0
        "Actual PTY displays retained narrative paragraphs and inert controls"

    fixture.TransformNextReply("/api/case.get", rewriteCase)

    let machine =
        CliCalls.verb fixture owner [ "case"; "get"; reference ] 0
        |> require "SUCCEEDED"

    Expect.equal
        (machine
            .GetProperty("state")
            .GetProperty("fields")
            .GetProperty("registration")
            .GetProperty("incidentDescription")
            .GetString())
        description
        "JSON retains exact unsafe-but-representable text rather than terminal escaping it"

    Expect.equal
        (machine.GetProperty("work").GetProperty("summary").GetString())
        summary
        "JSON summary is exact"

    fixture.TransformNextReply("/api/case.get", rewriteCase)

    let query =
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
        CliCalls.invoke
            fixture
            owner
            [ "session" ]
            (Array.concat [ query; [| 10uy |]; query; [| 10uy |] ])
            0

    Expect.equal ndjson.ExitCode 0 "Actual NDJSON reads retain independent frame boundaries"

    let rows =
        Encoding.UTF8
            .GetString(ndjson.StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)

    Expect.hasLength rows 2 "A paragraph does not split an NDJSON frame"
    use first = JsonDocument.Parse rows[0]

    Expect.equal
        (first.RootElement
            .GetProperty("result")
            .GetProperty("outcome")
            .GetProperty("data")
            .GetProperty("state")
            .GetProperty("fields")
            .GetProperty("registration")
            .GetProperty("incidentDescription")
            .GetString())
        description
        "NDJSON preserves retained text exactly"

    fixture.TransformNextReply("/api/reminder.get", rewriteReminder)

    let machineReminder =
        CliCalls.verb fixture owner [ "reminder"; "get"; reference; "--reminder-id"; reminder ] 0
        |> require "SUCCEEDED"

    Expect.equal
        (machineReminder
            .GetProperty("item")
            .GetProperty("reminder")
            .GetProperty("action")
            .GetString())
        action
        "Reminder JSON preserves retained text exactly"

    let formula = "=SUM(1,2)" + unsafe + "END-FORMULA"

    let rewriteQueue bytes =
        let root = JsonNode.Parse(bytes: byte array) |> nonNull

        let items =
            (((root["outcome"] |> nonNull)["data"] |> nonNull)["items"] |> nonNull).AsArray()

        ((items[0] |> nonNull)["reminder"] |> nonNull)["action"] <- JsonValue.Create formula
        Encoding.UTF8.GetBytes(root.ToJsonString())

    fixture.TransformNextReply("/api/reminder.list", rewriteQueue)

    let csv =
        CliCalls.invoke
            fixture
            owner
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

    Expect.equal csv.ExitCode 0 "Retained reminder CSV is produced by the actual published client"

    Expect.isTrue
        (Encoding.UTF8
            .GetString(csv.StandardOutput)
            .Contains("\"'" + formula + "\"", StringComparison.Ordinal))
        "Comma-bearing retained action is quoted and its formula prefix neutralized; remaining data stays exact"

    Expect.equal
        (readHead ())
        before
        "Display and machine reads append no synthetic business events"
