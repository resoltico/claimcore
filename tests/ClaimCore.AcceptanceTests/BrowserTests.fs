module ClaimCore.AcceptanceTests.BrowserTests

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open Expecto

let private engines =
    match Environment.GetEnvironmentVariable "CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE" with
    | null -> [ "chromium"; "firefox"; "webkit" ]
    | "chromium" -> [ "chromium" ]
    | "firefox" -> [ "firefox" ]
    | "webkit" -> [ "webkit" ]
    | _ -> invalidOp "Browser engine must be chromium, firefox or webkit."

let private specification =
    Environment.GetEnvironmentVariable "CLAIMCORE_ACCEPTANCE_BROWSER_SPEC"
    |> Option.ofObj

/// Only public source basenames/lines and standard result states leave the private report.
let private sourceLocation (value: JsonElement) =
    let mutable file = Unchecked.defaultof<JsonElement>
    let mutable line = Unchecked.defaultof<JsonElement>

    if
        value.ValueKind = JsonValueKind.Object
        && value.TryGetProperty("file", &file)
        && value.TryGetProperty("line", &line)
    then
        Some(
            (Path.GetFileName(file.GetString() |> nonNull) |> nonNull)
            + ":"
            + string (line.GetInt32())
        )
    else
        None

let private failedResults (spec: JsonElement) =
    spec.GetProperty("tests").EnumerateArray()
    |> Seq.collect (fun test -> test.GetProperty("results").EnumerateArray())
    |> Seq.choose (fun result ->
        match result.GetProperty("status").GetString() |> nonNull with
        | ("failed" | "timedOut" | "interrupted") as status ->
            let mutable location = Unchecked.defaultof<JsonElement>

            let source =
                if result.TryGetProperty("errorLocation", &location) then
                    sourceLocation location
                else
                    None

            Some(
                status
                + (source |> Option.map (fun value -> " " + value) |> Option.defaultValue "")
            )
        | _ -> None)
    |> Seq.distinct
    |> String.concat "; "

let private failedSources output =
    try
        use document =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "playwright.json")))

        let rec collect (suite: JsonElement) =
            let mutable children = Unchecked.defaultof<JsonElement>

            let nested =
                if suite.TryGetProperty("suites", &children) then
                    children.EnumerateArray() |> Seq.toList |> List.collect collect
                else
                    []

            let mutable specs = Unchecked.defaultof<JsonElement>

            let failed =
                if suite.TryGetProperty("specs", &specs) then
                    specs.EnumerateArray()
                    |> Seq.toList
                    |> List.filter (fun spec -> not (spec.GetProperty("ok").GetBoolean()))
                    |> List.map (fun spec ->
                        let definition =
                            sourceLocation spec |> Option.defaultValue "unknown source"

                        let failure = failedResults spec
                        definition + (if failure = "" then "" else " at " + failure))
                else
                    []

            nested @ failed

        collect document.RootElement |> List.distinct |> String.concat ", "
    with _ ->
        "standard report unavailable"

let private run engine =
    task {
        use fixture = new PublishedFixture.Fixture()
        do! fixture.Start()
        CliCalls.setup fixture
        let output = Path.Combine(fixture.Directory, engine)
        Directory.CreateDirectory output |> ignore
        let values = Dictionary<string, string>()
        values["CLAIMCORE_WEB_BASE_URL"] <- fixture.Origin
        values["CLAIMCORE_WEB_E2E_BACKEND_URL"] <- fixture.Backend
        values["CLAIMCORE_WEB_E2E_ENGINE"] <- engine
        values["CLAIMCORE_WEB_E2E_REDIRECT_STATE_DIR"] <- fixture.Directory
        values["CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR"] <- output

        values["CLAIMCORE_TEST_OIDC_CREDENTIALS"] <-
            Path.Combine(fixture.Directory, "credentials.json")

        values["CLAIMCORE_WEB_E2E_PRINCIPALS_FILE"] <-
            Path.Combine(fixture.Directory, "principals.json")

        let arguments =
            [ "exec"; "--"; "playwright"; "test" ]
            @ (specification |> Option.toList)
            @ [ "--project"; engine ]
            @ (if engine = "chromium" then [] else [ "--grep"; "@engine" ])

        let start = PublishedProcesses.start "npm" arguments values
        start.WorkingDirectory <- Path.Combine(fixture.Root, "web")

        let result =
            try
                PublishedProcesses.run (21 * 60_000) start None
            with :? ClaimCore.TestSupport.BoundedProcessFailure as failure ->
                FixtureFiles.write output "playwright.stdout" failure.StandardOutput |> ignore
                FixtureFiles.write output "playwright.stderr" failure.StandardError |> ignore
                reraise ()

        FixtureFiles.write output "playwright.stdout" result.StandardOutput |> ignore
        FixtureFiles.write output "playwright.stderr" result.StandardError |> ignore

        let sources =
            if result.ExitCode = 0 then
                ""
            else
                "; failed sources: " + failedSources output

        Expect.equal
            result.ExitCode
            0
            ("Playwright " + engine + sources + "; private evidence: " + output)

        if
            engine = "chromium"
            && (specification |> Option.forall (fun value -> value.Contains("unicode-parity")))
        then
            BrowserReadback.qualify fixture output

        fixture.Complete()
    }

[<Tests>]
let tests =
    testCaseAsync
        "published browser acceptance"
        (async {
            for engine in engines do
                do! run engine |> Async.AwaitTask
        })
