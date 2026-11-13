module ClaimCore.Tests.CliOfflineHelpTests

open ClaimCore.CommandLine

open ClaimCore.Cli
open System.Collections.Generic
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

let private invoke arguments source =
    CliOffline.invoke arguments (Dictionary<string, string>()) source

let private commandHelp language arguments =
    let result =
        invoke ("help" :: arguments @ [ "--format"; "text"; "--language"; language ]) None

    Expect.equal result.ExitCode 0 "Published selected human description"
    Expect.equal result.StandardError.Length 0 "No prose on stderr"
    Encoding.UTF8.GetString result.StandardOutput

let private selectedInputs () =
    // CliHelpTests covers the complete operation/input/language product in process.
    // CliUsageTests.localizedDiscovery checks every offline language with independent literals.
    for operation in CommandGrammar.operations do
        let output = commandHelp "en" (CommandGrammar.words operation)

        Expect.stringContains
            output
            (CommandGrammar.synopsis operation)
            "Published grammar follows semantic task declarations"

        let inputs = CommandGrammar.inputs operation

        for input in inputs do
            if input.Argument <> CommandArgument.Implicit then
                Expect.stringContains
                    output
                    (ClaimCore.Cli.ContractDiscovery.localized "en" input.LabelId)
                    "Published input labels survive"

// These historical grammar captures intentionally retain a noncanonical bare-host issuer.
// L6 now refuses their business syntax locally; they remain authored structural command examples.
let private noncanonicalPeopleExample arguments =
    match arguments with
    | "people" :: act :: _ ->
        List.contains
            act
            [
                "register"
                "grant"
                "revoke"
                "enable"
                "disable"
                "set-profile"
                "erase-profile"
            ]
        && (arguments
            |> List.windowed 2
            |> List.contains [ "--issuer"; "https://id.example" ])
    | _ -> false

let private principalRefusal arguments =
    let result = invoke (arguments @ [ "--dry-run"; "--format"; "json" ]) None
    Expect.equal result.ExitCode 2 "Noncanonical issuer is refused before dispatch"
    Expect.equal result.StandardError.Length 0 "The refusal is one machine frame"
    use document = JsonDocument.Parse result.StandardOutput
    let frame = document.RootElement
    Expect.equal (frame.GetProperty("delivery").GetString()) "NOT_SENT" "Offline refusal"

    Expect.isFalse
        (frame.TryGetProperty("operationId") |> fst)
        "An offline syntax refusal never invents an executable identity"

    let diagnostic = frame.GetProperty "diagnostic"
    Expect.equal (diagnostic.GetProperty("code").GetString()) "INVALID_INPUT" "Input diagnostic"
    Expect.equal (diagnostic.GetProperty("action").GetString()) "CORRECT_INPUT" "Repair action"
    let issues = diagnostic.GetProperty "issues"
    Expect.equal (issues.GetArrayLength()) 1 "Exactly the authored principal issue"
    Expect.equal (issues[0].GetProperty("input").GetString()) "input:principal.issuer" "Issuer target"

    Expect.equal
        (issues[0].GetProperty("code").GetString())
        "PRINCIPAL_FORMAT"
        "Canonical issuer rule"

let private commandExamples () =
    let rows =
        System.IO.File.ReadAllLines(
            System.IO.Path.Combine(
                ClaimCore.TestSupport.RepositoryRoot.find (),
                "tests/fixtures/cli-commands.txt"
            )
        )

    let captured =
        rows
        |> Array.map (fun row ->
            let arguments =
                row.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

            CommandGrammar.operations
            |> List.sortByDescending (CommandGrammar.words >> List.length)
            |> List.find (fun operation ->
                let words = CommandGrammar.words operation
                List.truncate words.Length arguments = words))
        |> Set.ofArray

    let expected = CommandGrammar.operations |> Set.ofList
    Expect.equal captured expected "Every semantic task has an independent captured frame"

    Expect.equal
        (rows |> Array.distinct |> Array.length)
        rows.Length
        "Captured invocation arguments are distinct"

    let mutable localRefusals = 0

    for row in rows do
        let arguments =
            row.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

        if noncanonicalPeopleExample arguments then
            localRefusals <- localRefusals + 1
            principalRefusal arguments
        else
            let result = invoke (arguments @ [ "--dry-run" ]) None
            Expect.equal result.ExitCode 0 row
            Expect.equal result.StandardError.Length 0 "Offline preparation emits only the frame"

            Expect.isOk
                (Frames.admit result.StandardOutput)
                "Published dry-run output is an admissible frame"

    Expect.equal localRefusals 8 "All eight original noncanonical issuer examples remain exercised"

let private diagnosticRoute () =
    let result = invoke [ "call" ] (Some(Encoding.UTF8.GetBytes "{invalid-frame"))

    Expect.equal result.ExitCode 2 "Malformed input preserves protocol refusal"
    Expect.equal result.StandardError.Length 0 "Exactly one machine frame, no prose"
    use frame = JsonDocument.Parse result.StandardOutput
    let root = frame.RootElement

    Expect.equal
        (root.GetProperty("result").GetProperty("localFailure").GetProperty("code").GetString())
        "INVALID_FRAME"
        "Observed protocol category"

    Expect.equal (root.GetProperty("delivery").GetString()) "NOT_SENT" "No dispatch"
    let id = InputDiagnostics.protocolId

    for language in LocalizationPolicy.languages do
        let help = invoke [ "help"; id; "--language"; language ] None
        Expect.equal help.ExitCode 0 "Returned ID is an admitted core help route"
        let guidance = Encoding.UTF8.GetString help.StandardOutput

        for route in
            [
                "claimcore schema invocation"
                "claimcore schema response"
                "claimcore help call"
                "claimcore help session"
                "docs/integrating.md#cli-use"
            ] do
            Expect.stringContains guidance route "Repairable framing and schema guidance"

[<Tests>]
let tests =
    testCase "offline CLI help and captured dry-runs" (fun () ->
        selectedInputs ()
        commandExamples ()
        diagnosticRoute ())
