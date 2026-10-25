module internal ClaimCore.AcceptanceTests.CliDescriptions

open System.Collections.Generic
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Contract

let private invoke (fixture: PublishedFixture.Fixture) arguments source =
    ProcessRunner.dotnet 8_000 fixture.Cli arguments (Dictionary<string, string>()) source

let private description fixture language arguments =
    let result =
        invoke
            fixture
            ("describe" :: arguments @ [ "--format"; "text"; "--language"; language ])
            None

    Expect.equal result.ExitCode 0 "Published selected human description"
    Expect.equal result.StandardError.Length 0 "No prose on stderr"
    Encoding.UTF8.GetString result.StandardOutput

let private selectedInputs fixture =
    for language in LocalizationPolicy.languages do
        for operation in OperationCatalog.all do
            let output = description fixture language (OperationCatalog.words operation)

            Expect.stringContains
                output
                (OperationCatalog.synopsis operation)
                "Published grammar matches the catalog"

            let inputs, _ = OperationCatalog.derive operation

            for input in inputs do
                if input.Spec.Argument <> CatalogArgument.Implicit then
                    Expect.stringContains
                        output
                        (ClaimCore.Cli.ContractDiscovery.localized language input.Binding.LabelId)
                        "Published input labels survive"

let private frameOracle fixture =
    let rows =
        System.IO.File.ReadAllLines(
            System.IO.Path.Combine(
                ClaimCore.TestSupport.RepositoryRoot.find (),
                "tests/fixtures/cli-frames.tsv"
            )
        )

    Expect.equal rows.Length 30 "Complete captured oracle"

    for row in rows do
        let cells = row.Split('\t', 2)

        let arguments =
            cells[0].Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray

        let result = invoke fixture (arguments @ [ "--dry-run" ]) None
        Expect.equal result.ExitCode 0 cells[0]
        Expect.equal result.StandardError.Length 0 "Offline preparation emits only the frame"

        Expect.isTrue
            (System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse cells[1],
                System.Text.Json.Nodes.JsonNode.Parse result.StandardOutput
            ))
            cells[0]

let private diagnosticRoute fixture =
    let result =
        invoke fixture [ "call" ] (Some(Encoding.UTF8.GetBytes "{invalid-frame"))

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
        let help = invoke fixture [ "help"; id; "--language"; language ] None
        Expect.equal help.ExitCode 0 "Returned ID is an admitted core help route"
        let guidance = Encoding.UTF8.GetString help.StandardOutput

        for route in
            [
                "claimcore schema invocation"
                "claimcore schema response"
                "claimcore help call"
                "claimcore help session"
                "NDJSON"
                "EOF"
            ] do
            Expect.stringContains guidance route "Repairable framing and schema guidance"


let qualify fixture =
    selectedInputs fixture
    frameOracle fixture
    diagnosticRoute fixture
