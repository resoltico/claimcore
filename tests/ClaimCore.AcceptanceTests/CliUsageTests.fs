module ClaimCore.AcceptanceTests.CliUsageTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Collections.Generic
open Expecto
open ClaimCore.Contract
open ClaimCore.TestSupport

type private Example =
    {
        Name: string
        Arguments: string list
        Message: string
        Values: (string * string) list
        Suggestion: string option
    }

let private close = [ "case"; "close"; "REF"; "--expected-revision"; "1" ]

let private correct =
    [
        "case"
        "correct"
        "REF"
        "--expected-revision"
        "1"
        "--registration"
        "keep"
        "--decision"
        "keep"
        "--payment"
        "keep"
    ]

let private examples =
    [
        {
            Name = "missing revision"
            Arguments = [ "case"; "close"; "REF" ]
            Message = "missingOption"
            Values = [ "flag", "--expected-revision" ]
            Suggestion = None
        }
        {
            Name = "misspelled option"
            Arguments = [ "case"; "close"; "REF"; "--expected-revison"; "1" ]
            Message = "unknownOption"
            Values = [ "flag", "--expected-revison" ]
            Suggestion = Some "--expected-revision"
        }
        {
            Name = "misspelled verb"
            Arguments = [ "case"; "closee"; "REF" ]
            Message = "unknownCommand"
            Values = [ "command", "case closee" ]
            Suggestion = Some "claimcore case close"
        }
        {
            Name = "missing reference"
            Arguments = [ "case"; "close"; "--expected-revision"; "1" ]
            Message = "missingOperand"
            Values = [ "operand", "CASE-REFERENCE" ]
            Suggestion = None
        }
        {
            Name = "extra operand"
            Arguments = close @ [ "extra" ]
            Message = "extraOperand"
            Values = [ "operand", "extra" ]
            Suggestion = None
        }
        {
            Name = "invalid integer"
            Arguments = [ "case"; "close"; "REF"; "--expected-revision"; "three" ]
            Message = "invalidInteger"
            Values = [ "flag", "--expected-revision"; "value", "three" ]
            Suggestion = None
        }
        {
            Name = "invalid choice"
            Arguments =
                [
                    "case"
                    "correct"
                    "REF"
                    "--expected-revision"
                    "1"
                    "--registration"
                    "swap"
                    "--decision"
                    "keep"
                    "--payment"
                    "keep"
                ]
            Message = "invalidChoice"
            Values = [ "flag", "--registration"; "value", "swap"; "allowed", "keep|replace" ]
            Suggestion = None
        }
        {
            Name = "inapplicable payment input"
            Arguments = correct @ [ "--payment-date"; "2026-01-01" ]
            Message = "notApplicable"
            Values = [ "flag", "--payment-date"; "because", "--payment keep" ]
            Suggestion = None
        }
        {
            Name = "duplicate option"
            Arguments = close @ [ "--expected-revision"; "2" ]
            Message = "duplicateOption"
            Values = [ "flag", "--expected-revision" ]
            Suggestion = None
        }
        {
            Name = "dangling option"
            Arguments = [ "case"; "close"; "REF"; "--expected-revision" ]
            Message = "missingValue"
            Values = [ "flag", "--expected-revision" ]
            Suggestion = None
        }
    ]

let private frozen (rows: JsonElement) language key values =
    let row =
        rows.EnumerateArray()
        |> Seq.find (fun row ->
            row.GetProperty("language").GetString() = language
            && row.GetProperty("key").GetString() = "cli.usage." + key)

    (row.GetProperty("expected").GetString() |> nonNull, values)
    ||> List.fold (fun text (name, value) -> text.Replace("«" + name + "»", value))

let private qualify example () =
    // Each case owns its publication. The sequential group avoids shared producer writes.
    let directory = Directory.CreateTempSubdirectory("claimcore-cli-usage-")

    try
        let root = RepositoryRoot.find ()
        let cli = PublishedProcesses.publish root directory.FullName "ClaimCore.Cli"

        use expected =
            JsonDocument.Parse(
                File.ReadAllBytes(Path.Combine(root, "tests/fixtures/rendered-messages.json"))
            )

        for language in LocalizationPolicy.languages do
            let result =
                ProcessRunner.dotnet
                    8_000
                    cli
                    (example.Arguments @ [ "--language"; language ])
                    (Dictionary<string, string>())
                    None

            Expect.equal result.ExitCode 64 "Usage refuses before configuration or dispatch"
            Expect.equal result.StandardOutput.Length 0 "Invalid input emits no request/reply"
            let text = Encoding.UTF8.GetString result.StandardError

            Expect.stringContains
                text
                (frozen expected.RootElement language example.Message example.Values)
                "The specific localized error agrees with independent frozen text"

            example.Suggestion
            |> Option.iter (fun suggestion ->
                Expect.stringContains
                    text
                    (frozen expected.RootElement language "suggestion" [ "suggestion", suggestion ])
                    "Relevant spelling suggestion")

            let lines = text.TrimEnd().Split('\n')
            Expect.isLessThanOrEqual lines.Length 8 "Concise guidance"

            if example.Message <> "unknownCommand" then
                Expect.stringContains
                    lines[lines.Length - 1]
                    ("claimcore case " + example.Arguments[1])
                    "Only the matched synopsis follows the errors"

            if example.Message <> "unknownOption" then
                Expect.equal lines.Length 2 "One problem and one synopsis/suggestion"
    finally
        directory.Delete true

[<Tests>]
let tests =
    examples
    |> List.map (fun example -> testCase example.Name (qualify example))
    |> testList "published CLI usage errors"
    |> testSequenced
