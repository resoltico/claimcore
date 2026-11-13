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

/// Console summaries use source basenames/lines and result states; synthetic attachments are retained separately.
let private sourceLocation (value: JsonElement) =
    let mutable file = Unchecked.defaultof<JsonElement>
    let mutable line = Unchecked.defaultof<JsonElement>

    if
        value.ValueKind = JsonValueKind.Object
        && value.TryGetProperty("file", &file)
        && value.TryGetProperty("line", &line)
    then
        Some(Path.GetFileName(file.GetString() |> nonNull) + ":" + string (line.GetInt32()))
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
                    let mutable errors = Unchecked.defaultof<JsonElement>

                    if result.TryGetProperty("errors", &errors) then
                        errors.EnumerateArray()
                        |> Seq.tryPick (fun error ->
                            let mutable location = Unchecked.defaultof<JsonElement>

                            if error.TryGetProperty("location", &location) then
                                sourceLocation location
                            else
                                None)
                    else
                        None

            let actionability =
                let mutable errors = Unchecked.defaultof<JsonElement>

                if result.TryGetProperty("errors", &errors) then
                    errors.EnumerateArray()
                    |> Seq.collect (fun error ->
                        let mutable message = Unchecked.defaultof<JsonElement>

                        if error.TryGetProperty("message", &message) then
                            let text = message.GetString() |> nonNull

                            [
                                "outside of the viewport"
                                "intercepts pointer events"
                                "not stable"
                                "not visible"
                            ]
                            |> List.filter text.Contains
                        else
                            [])
                    |> Seq.distinct
                    |> String.concat ","
                else
                    ""

            Some(
                status
                + (source |> Option.map (fun value -> " " + value) |> Option.defaultValue "")
                + (if actionability = "" then
                       ""
                   else
                       " actionability=" + actionability)

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

// Only failed-spec attachments and process output are exported; authentication state stays private.
let private retainFailureFiles output destination =
    for source in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories) do
        let name = Path.GetFileName source

        if
            name = "trace.zip"
            || name = "error-context.md"
            || name = "playwright.stdout"
            || name = "playwright.stderr"
            || (name.StartsWith("test-failed-", StringComparison.Ordinal)
                && name.EndsWith(".png", StringComparison.Ordinal))
        then
            let target = Path.Combine(destination, Path.GetRelativePath(output, source))
            Directory.CreateDirectory(Path.GetDirectoryName target |> nonNull) |> ignore
            File.Copy(source, target, true)

let rec private includesUnicode (suite: JsonElement) =
    let mutable children = Unchecked.defaultof<JsonElement>
    let mutable specs = Unchecked.defaultof<JsonElement>

    (suite.TryGetProperty("specs", &specs)
     && (specs.EnumerateArray()
         |> Seq.exists (fun spec ->
             spec.GetProperty("file").GetString()
             |> nonNull
             |> fun file -> file.Contains("unicode-parity"))))
    || (suite.TryGetProperty("suites", &children)
        && (children.EnumerateArray() |> Seq.exists includesUnicode))


let private run engine =
    task {
        use fixture = new PublishedFixture.Fixture()

        let failureReport =
            Environment.GetEnvironmentVariable "CLAIMCORE_TEST_REPORT_DIRECTORY"
            |> Option.ofObj
            |> Option.map (fun report ->
                Path.Combine(report, "failed-browser", engine, Path.GetFileName fixture.Directory))

        failureReport
        |> Option.iter (fun report ->
            fixture.RetainServiceFailureEvidence(Path.Combine(report, "service")))

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
            @ (Environment.GetEnvironmentVariable "CLAIMCORE_ACCEPTANCE_BROWSER_SHARD"
               |> Option.ofObj
               |> Option.filter (fun _ -> engine = "chromium")
               |> Option.map (fun shard -> [ "--shard"; shard ])
               |> Option.defaultValue [])
            @ (if engine = "chromium" then [] else [ "--grep"; "@engine" ])

        let start = PublishedProcesses.start "npm" arguments values
        start.WorkingDirectory <- Path.Combine(fixture.Root, "web")

        let result =
            try
                PublishedProcesses.run (21 * 60_000) start None
            with :? ClaimCore.TestSupport.BoundedProcessFailure as failure ->
                FixtureFiles.write output "playwright.stdout" failure.StandardOutput |> ignore
                FixtureFiles.write output "playwright.stderr" failure.StandardError |> ignore
                failureReport |> Option.iter (retainFailureFiles output)
                reraise ()

        FixtureFiles.write output "playwright.stdout" result.StandardOutput |> ignore
        FixtureFiles.write output "playwright.stderr" result.StandardError |> ignore

        if result.ExitCode <> 0 then
            failureReport |> Option.iter (retainFailureFiles output)

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
            use report =
                JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "playwright.json")))

            if includesUnicode report.RootElement then
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
