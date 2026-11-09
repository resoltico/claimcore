module internal ClaimCore.AcceptanceTests.CliHelp

open ClaimCore.Cli
open System.Collections.Generic
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

let private invoke cli arguments source =
    ProcessRunner.dotnet 8_000 cli arguments (Dictionary<string, string>()) source

let private commandHelp cli language arguments =
    let result =
        invoke cli ("help" :: arguments @ [ "--format"; "text"; "--language"; language ]) None

    Expect.equal result.ExitCode 0 "Published selected human description"
    Expect.equal result.StandardError.Length 0 "No prose on stderr"
    Encoding.UTF8.GetString result.StandardOutput

let private selectedInputs cli =
    // CliHelpTests covers the complete operation/input/language product in process.
    // CliUsageTests.localizedDiscovery checks every published language with independent literals.
    for operation in CommandGrammar.operations do
        let output = commandHelp cli "en" (CommandGrammar.words operation)

        Expect.stringContains
            output
            (CommandGrammar.synopsis operation)
            "Published grammar matches the catalog"

        let inputs, _ = CommandGrammar.derive operation

        for input in inputs do
            if input.Argument <> CatalogArgument.Implicit then
                Expect.stringContains
                    output
                    (ClaimCore.Cli.ContractDiscovery.localized "en" input.Binding.LabelId)
                    "Published input labels survive"

// These historical grammar captures intentionally retain a noncanonical bare-host issuer.
// L6 now refuses their business syntax locally; they remain authored structural command examples.
let private noncanonicalAuthorityExample arguments =
    match arguments with
    | "authority" :: act :: _ ->
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

let private principalRefusal cli arguments =
    let result = invoke cli (arguments @ [ "--dry-run"; "--format"; "json" ]) None
    Expect.equal result.ExitCode 2 "Noncanonical issuer is refused before dispatch"
    Expect.equal result.StandardError.Length 0 "The refusal is one machine frame"
    use document = JsonDocument.Parse result.StandardOutput
    let frame = document.RootElement
    Expect.equal (frame.GetProperty("endpoint").GetString()) "operation.execute" "Authored endpoint"
    Expect.equal (frame.GetProperty("delivery").GetString()) "NOT_SENT" "Offline refusal"
    let outcome = frame.GetProperty("result").GetProperty("outcome")
    Expect.equal (outcome.GetProperty("kind").GetString()) "REJECTED" "Business syntax refusal"
    let diagnostic = outcome.GetProperty "data"
    Expect.equal (diagnostic.GetProperty("code").GetString()) "INVALID_INPUT" "Input diagnostic"
    Expect.equal (diagnostic.GetProperty("action").GetString()) "CORRECT_INPUT" "Repair action"
    let issues = diagnostic.GetProperty "issues"
    Expect.equal (issues.GetArrayLength()) 1 "Exactly the authored principal issue"
    Expect.equal (issues[0].GetProperty("field").GetString()) "principal.issuer" "Issuer target"

    Expect.equal
        (issues[0].GetProperty("code").GetString())
        "PRINCIPAL_FORMAT"
        "Canonical issuer rule"

let private commandExamples cli =
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
    Expect.equal captured expected "Every catalog operation has an independent captured frame"

    Expect.equal
        (rows |> Array.distinct |> Array.length)
        rows.Length
        "Captured invocation arguments are distinct"

    let mutable localRefusals = 0

    for row in rows do
        let arguments =
            row.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

        if noncanonicalAuthorityExample arguments then
            localRefusals <- localRefusals + 1
            principalRefusal cli arguments
        else
            let result = invoke cli (arguments @ [ "--dry-run" ]) None
            Expect.equal result.ExitCode 0 row
            Expect.equal result.StandardError.Length 0 "Offline preparation emits only the frame"

            Expect.isOk
                (Frames.admit result.StandardOutput)
                "Published dry-run output is an admissible frame"

    Expect.equal localRefusals 8 "All eight original noncanonical issuer examples remain exercised"

let private diagnosticRoute cli =
    let result = invoke cli [ "call" ] (Some(Encoding.UTF8.GetBytes "{invalid-frame"))

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
        let help = invoke cli [ "help"; id; "--language"; language ] None
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

let qualify cli =
    selectedInputs cli
    commandExamples cli
    diagnosticRoute cli
