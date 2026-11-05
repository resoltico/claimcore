module ClaimCore.AcceptanceTests.BrowserTests

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Collections.Generic
open System.IO
open System.Threading.Tasks
open Expecto

let private selectEngines (value: string | null) =
    match value with
    | null -> [ "chromium"; "firefox"; "webkit" ]
    | "chromium" -> [ "chromium" ]
    | "firefox" -> [ "firefox" ]
    | "webkit" -> [ "webkit" ]
    | _ -> invalidOp "Acceptance browser engine must be chromium, firefox or webkit."

[<RequireQualifiedAccess>]
type private BrowserScope =
    | Complete
    | Smoke
    | Diagnostic of specifications: string list

let private selectScope
    (value: string | null)
    (specification: string | null)
    (engines: string list)
    =
    let oneEngine () =
        if engines.Length <> 1 then
            invalidOp "Partial browser scope requires one explicit acceptance browser engine."

    let noSpecification () =
        if specification <> null then
            invalidOp "A browser specification is only valid with diagnostic scope."

    match value with
    | null
    | "complete" ->
        noSpecification ()
        BrowserScope.Complete
    | "smoke" ->
        noSpecification ()
        oneEngine ()
        BrowserScope.Smoke
    | "diagnostic" ->
        oneEngine ()

        match specification with
        | null -> invalidOp "Diagnostic scope requires existing *.spec.ts basenames."
        | value ->
            let names = value.Split ',' |> Array.toList

            if
                names
                |> List.exists (fun name ->
                    not (Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9_.-]*\.spec\.ts\z")))
                || (Set.ofList names).Count <> names.Length
            then
                invalidOp
                    "Diagnostic specifications must be unique comma-separated *.spec.ts basenames."

            BrowserScope.Diagnostic names
    | _ -> invalidOp "Acceptance browser scope must be complete, smoke or diagnostic."

let private engines =
    Environment.GetEnvironmentVariable "CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE"
    |> selectEngines

let private scope =
    selectScope
        (Environment.GetEnvironmentVariable "CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE")
        (Environment.GetEnvironmentVariable "CLAIMCORE_ACCEPTANCE_BROWSER_SPEC")
        engines

let private smokeSpecifications =
    [
        "authority-uncertainty.spec.ts"
        "authority-grants.spec.ts"
        "act-lifecycle.spec.ts"
        "loading-boundaries.spec.ts"
        "localization-correction.spec.ts"
        "localization-editor.spec.ts"
        "localization-pseudolocale.spec.ts"
        "localization-session-history.spec.ts"
        "narrow-role.spec.ts"
        "recovery-journal.spec.ts"
        "resolution.spec.ts"
        "reply-correlation.spec.ts"
        "work-surface-foundations.spec.ts"
        "workspace-recovery.spec.ts"
        "stewardship-authority.spec.ts"
        "stewardship-localization.spec.ts"
        "uncertain-execution.spec.ts"
        "unicode-parity.spec.ts"
    ]

let private scopeName =
    function
    | BrowserScope.Complete -> "complete"
    | BrowserScope.Smoke -> "smoke"
    | BrowserScope.Diagnostic _ -> "diagnostic"

let private scopeDescription =
    function
    | BrowserScope.Complete -> "complete engine suite"
    | BrowserScope.Smoke -> "smoke; full browser qualification remains unrun"
    | BrowserScope.Diagnostic names ->
        "diagnostic "
        + String.concat ", " names
        + "; full browser qualification remains unrun"

let private specifications =
    function
    | BrowserScope.Complete -> []
    | BrowserScope.Smoke -> smokeSpecifications
    | BrowserScope.Diagnostic names -> names

let private requiresSpecification selected name =
    selected = BrowserScope.Complete || List.contains name (specifications selected)

let private expectedFiles root selected =
    let directory = Path.Combine(root, "web", "e2e")

    let all =
        Directory.EnumerateFiles(directory, "*.spec.ts", SearchOption.AllDirectories)

    match selected with
    | BrowserScope.Complete -> all |> Seq.map Path.GetFullPath |> Set.ofSeq
    | BrowserScope.Smoke ->
        smokeSpecifications
        |> Seq.map (fun file -> Path.GetFullPath(Path.Combine(directory, file)))
        |> Set.ofSeq
    | BrowserScope.Diagnostic names ->
        names
        |> List.map (fun basename ->
            match
                all |> Seq.filter (fun file -> Path.GetFileName file = basename) |> Seq.toList
            with
            | [ file ] -> Path.GetFullPath file
            | _ -> invalidOp "Each diagnostic basename must identify exactly one existing file.")
        |> Set.ofList

let private fallbackReportDirectory =
    lazy
        Path.Combine(
            ClaimCore.TestSupport.RepositoryRoot.find (),
            "artifacts",
            "verification",
            "native." + Guid.NewGuid().ToString("N")
        )

let private reportDirectory () =
    match Environment.GetEnvironmentVariable "CLAIMCORE_TEST_REPORT_DIRECTORY" with
    | null -> fallbackReportDirectory.Value
    | value -> value

let private report selected files output engine =
    let count, duration =
        BrowserCompletion.read files engine (Path.Combine(output, "playwright.json"))

    let directory = reportDirectory ()

    Directory.CreateDirectory directory |> ignore
    let measurement = Path.Combine(output, "loading-measurements.json")

    if File.Exists measurement then
        File.Copy(measurement, Path.Combine(directory, "loading-" + engine + ".json"), true)

    let featureMeasurement = Path.Combine(output, "loading-new-work.json")

    if requiresSpecification selected "case-work.spec.ts" then
        Expect.isTrue
            (File.Exists featureMeasurement)
            "Selected scope retains the required work-editor measurement"

    if File.Exists featureMeasurement then
        File.Copy(
            featureMeasurement,
            Path.Combine(directory, "loading-work-" + engine + ".json"),
            true
        )

    JsonSerializer.SerializeToUtf8Bytes
        {|
            runId = Path.GetFileName directory
            engine = engine
            scope = scopeName selected
            fullQualification = selected = BrowserScope.Complete
            specifications = specifications selected
            passed = count
            duration = duration
        |}
    |> FixtureFiles.write directory ("browser-" + engine + ".json")
    |> ignore

    Console.WriteLine(
        $"Published {engine} ({scopeDescription selected}): {count} passed; {duration}"
    )

let private completionFixture nested =
    let test =
        """{"projectName":"chromium","expectedStatus":"passed","status":"expected","results":[{"status":"passed","retry":0,"errors":[]}]}"""

    let spec id file =
        $"""{{"id":"{id}","file":"{file}","ok":true,"tests":[{test}]}}"""

    let specifications =
        [ spec "a" "synthetic-a.spec.ts"; spec "b" "synthetic-b.spec.ts" ]
        |> String.concat ","

    let suites =
        if nested then
            $"""[{{"specs":[],"suites":[{{"specs":[{specifications}]}}]}}]"""
        else
            $"""[{{"specs":[{specifications}]}}]"""

    let directory = JsonSerializer.Serialize(Path.GetTempPath())

    $"""{{"config":{{"rootDir":{directory},"forbidOnly":true,"projects":[{{"name":"chromium","retries":0,"repeatEach":1}}]}},"errors":[],"suites":{suites},"stats":{{"duration":1200,"expected":2,"unexpected":0,"flaky":0,"skipped":0}}}}"""

let private completionFileControls (original: string) files =
    let directory =
        Directory.CreateTempSubdirectory("claimcore-browser-report-control-")

    try
        let path = Path.Combine(directory.FullName, "playwright.json")
        File.WriteAllText(path, original)
        Expect.equal (BrowserCompletion.read files "chromium" path) (2, "1.2s") "Owned report reads"

        let resize length =
            use stream = File.OpenWrite path
            stream.SetLength length

        resize 8_388_609L

        Expect.throws
            (fun () -> BrowserCompletion.read files "chromium" path |> ignore)
            "Oversize report rejects before allocation"

        resize 0L

        Expect.throws
            (fun () -> BrowserCompletion.read files "chromium" path |> ignore)
            "Empty report rejects"
    finally
        directory.Delete true

let private completionControls () =
    let original = completionFixture false

    let files =
        [ "synthetic-a.spec.ts"; "synthetic-b.spec.ts" ]
        |> List.map (fun file -> Path.GetFullPath(Path.Combine(Path.GetTempPath(), file)))
        |> Set.ofList

    let inspect (text: string) =
        BrowserCompletion.inspect files "chromium" (Encoding.UTF8.GetBytes text)

    Expect.equal (inspect original) (2, "1.2s") "Complete structured execution is admitted"
    Expect.equal (inspect (completionFixture true)) (2, "1.2s") "Nested suites are traversed"
    completionFileControls original files

    for before, after in
        [
            "synthetic-b.spec.ts", "omitted.spec.ts"
            "\"specs\":[", "\"specs\":[],\"unused\":["
            "\"results\":[{\"status\":\"passed\",\"retry\":0,\"errors\":[]}]", "\"results\":[]"
            "\"status\":\"passed\"", "\"status\":\"skipped\""
            "\"retry\":0", "\"retry\":1"
            "\"retry\":0,\"errors\":[]", "\"retry\":0,\"errors\":[{}]"
            "\"tests\":[", "\"tests\":[],\"unused\":["
            "\"projectName\":\"chromium\"", "\"projectName\":\"firefox\""
            "\"status\":\"expected\"", "\"status\":\"flaky\""
            "\"errors\":[]", "\"errors\":[{}]"
            "\"forbidOnly\":true", "\"forbidOnly\":false"
            "\"retries\":0", "\"retries\":1"
            "\"repeatEach\":1", "\"repeatEach\":2"
            "\"id\":\"b\"", "\"id\":\"a\""
            "\"expected\":2", "\"expected\":1"
            "\"results\":[{\"status\":\"passed\",\"retry\":0,\"errors\":[]}]",
            "\"results\":[{\"status\":\"passed\",\"retry\":0,\"errors\":[]},{\"status\":\"passed\",\"retry\":0,\"errors\":[]}]"
        ] do
        let changed = original.Replace(before, after, StringComparison.Ordinal)
        Expect.notEqual changed original "The rejecting control changes its intended input"
        Expect.throws (fun () -> inspect changed |> ignore) "Incomplete execution is rejected"

let private selectionControls () =
    Expect.equal
        (selectEngines null)
        [ "chromium"; "firefox"; "webkit" ]
        "Default keeps every engine"

    for engine in [ "chromium"; "firefox"; "webkit" ] do
        Expect.equal (selectEngines engine) [ engine ] "Explicit isolated engine job"

    for value in [ ""; "all"; "unknown" ] do
        Expect.throws
            (fun () -> selectEngines value |> ignore)
            "Invalid scope cannot silently omit engines"

        Expect.throws
            (fun () -> selectScope value null [ "chromium" ] |> ignore)
            "Invalid campaign cannot silently shorten work"

    Expect.equal
        (selectScope null null (selectEngines null))
        BrowserScope.Complete
        "Default remains complete"

    Expect.equal
        (selectScope "complete" null [ "chromium" ])
        BrowserScope.Complete
        "Complete scope is explicit"

    Expect.equal
        (selectScope "smoke" null [ "chromium" ])
        BrowserScope.Smoke
        "Smoke is explicitly selected"

let private runSelected (qualify: string -> Task<unit>) selected =
    task {
        for engine in selected do
            do! qualify engine
    }

let private orchestrationControls () =
    let selected = [ "chromium"; "firefox"; "webkit" ]
    let observed = ResizeArray<string>()

    let complete engine =
        observed.Add engine
        Task.FromResult()

    (runSelected complete selected).GetAwaiter().GetResult()
    Expect.equal (Seq.toList observed) selected "Success executes every selected engine in order"

    for refused, expected in [ "chromium", [ "chromium" ]; "firefox", [ "chromium"; "firefox" ] ] do
        observed.Clear()

        let qualify engine =
            observed.Add engine

            if engine = refused then
                Task.FromException<unit>(InvalidOperationException "Synthetic engine refusal.")
            else
                Task.FromResult()

        Expect.throws
            (fun () -> (runSelected qualify selected).GetAwaiter().GetResult())
            "An engine failure remains a failing browser qualification"

        Expect.equal (Seq.toList observed) expected "Failure never starts later expensive fixtures"

let private timeoutMinutes =
    function
    | BrowserScope.Complete -> 40
    | BrowserScope.Diagnostic _ -> 20
    | BrowserScope.Smoke -> 2

let private processTimeout selected = (timeoutMinutes selected + 1) * 60_000

let private browserArguments selected engine =
    let files =
        match selected with
        | BrowserScope.Diagnostic names ->
            names |> List.map (fun name -> @"(?:^|[/\\])" + Regex.Escape name + "$")
        | _ -> specifications selected

    [ "exec"; "--"; "playwright"; "test" ]
    @ files
    @ [ "--project"; engine; "--forbid-only" ]
    @ [
        match selected with
        | BrowserScope.Diagnostic _ -> "--max-failures=0"
        | _ -> "--max-failures=1"
    ]
    @ [ $"--global-timeout={timeoutMinutes selected * 60_000}" ]

let private diagnosticControls () =
    let root = Directory.CreateTempSubdirectory("claimcore-browser-selection-")
    let directory = Path.Combine(root.FullName, "web", "e2e")
    Directory.CreateDirectory directory |> ignore
    let first = Path.Combine(directory, "synthetic-a.spec.ts")
    let second = Path.Combine(directory, "synthetic-b.spec.ts")
    File.WriteAllText(first, "")
    File.WriteAllText(second, "")

    try
        let selected = selectScope "diagnostic" "synthetic-a.spec.ts" [ "chromium" ]
        let files = expectedFiles root.FullName selected

        Expect.equal
            files
            (Set.singleton first)
            "Diagnostic inventory is the one actual existing file"

        Expect.equal (scopeName selected) "diagnostic" "A diagnostic is never a complete report"

        Expect.stringContains
            (scopeDescription selected)
            "remains unrun"
            "Partial scope is explicit"

        Expect.equal
            (expectedFiles root.FullName BrowserScope.Complete)
            (Set.ofList [ first; second ])
            "Default inventory keeps every specification"

        Expect.equal smokeSpecifications.Length 18 "Existing smoke inventory remains unchanged"

        let invalidSelections: (string * (string | null) * string list) list =
            [
                "diagnostic", null, [ "chromium" ]
                "diagnostic", "", [ "chromium" ]
                "diagnostic", "../synthetic-a.spec.ts", [ "chromium" ]
                "diagnostic", "/synthetic-a.spec.ts", [ "chromium" ]
                "diagnostic", @"nested\synthetic-a.spec.ts", [ "chromium" ]
                "diagnostic", "--project=webkit", [ "chromium" ]
                "diagnostic", "synthetic-*.spec.ts", [ "chromium" ]
                "diagnostic", "synthetic-a.spec.ts\n", [ "chromium" ]
                "diagnostic", "synthetic-a.spec.ts", selectEngines null
                "complete", "synthetic-a.spec.ts", [ "chromium" ]
                "smoke", "synthetic-a.spec.ts", [ "chromium" ]
                "smoke", null, selectEngines null
            ]

        for campaign, specification, engines in invalidSelections do
            Expect.throws
                (fun () -> selectScope campaign specification engines |> ignore)
                "Invalid selection cannot reach a fixture"

        let batch =
            selectScope "diagnostic" "synthetic-b.spec.ts,synthetic-a.spec.ts" [ "chromium" ]

        let batchFiles = expectedFiles root.FullName batch

        Expect.equal
            batchFiles
            (Set.ofList [ first; second ])
            "Every selected basename owns its actual file"

        Expect.equal
            (specifications batch)
            [ "synthetic-b.spec.ts"; "synthetic-a.spec.ts" ]
            "Authored order is retained"

        for invalid in
            [
                ",synthetic-a.spec.ts"
                "synthetic-a.spec.ts,"
                "synthetic-a.spec.ts,,synthetic-b.spec.ts"
                "synthetic-a.spec.ts,synthetic-a.spec.ts"
                "synthetic-a.spec.ts,../synthetic-b.spec.ts"
                "synthetic-a.spec.ts,--project=webkit"
                "synthetic-a.spec.ts,synthetic-*.spec.ts"
                "synthetic-a.spec.ts,synthetic-b.spec.ts\n"
                "synthetic-a.spec.ts, synthetic-b.spec.ts"
            ] do
            Expect.throws
                (fun () -> selectScope "diagnostic" invalid [ "chromium" ] |> ignore)
                "Malformed batches fail before setup"

        for campaign in [ BrowserScope.Complete; BrowserScope.Smoke ] do
            let arguments = browserArguments campaign "chromium"
            Expect.contains arguments "--max-failures=1" "Qualification and smoke still fail fast"

            Expect.isFalse
                (List.contains "--max-failures=0" arguments)
                "Only diagnostics collect every failure"

        for campaign, minutes, argument, nativeMilliseconds in
            [
                BrowserScope.Complete, 40, "--global-timeout=2400000", 2_460_000
                selected, 20, "--global-timeout=1200000", 1_260_000
                BrowserScope.Smoke, 2, "--global-timeout=120000", 180_000
            ] do
            Expect.equal
                (timeoutMinutes campaign)
                minutes
                "Each scope owns its explicit execution cap"

            let arguments = browserArguments campaign "chromium"

            Expect.equal
                (arguments
                 |> List.filter (fun value ->
                     value.StartsWith("--global-timeout=", StringComparison.Ordinal)))
                [ argument ]
                "Exactly one scope-specific Playwright cap is passed"

            Expect.equal
                (processTimeout campaign)
                nativeMilliseconds
                "Native cleanup allows exactly one additional minute"

        let batchArguments = browserArguments batch "chromium"

        Expect.equal
            (batchArguments |> List.skip 4 |> List.take 2)
            [ @"(?:^|[/\\])synthetic-b\.spec\.ts$"; @"(?:^|[/\\])synthetic-a\.spec\.ts$" ]
            "Every selected file becomes an escaped exact selector"

        Expect.contains
            batchArguments
            "--max-failures=0"
            "Diagnostics collect all selected failures"

        for names in [ "absent.spec.ts"; "synthetic-a.spec.ts,absent.spec.ts" ] do
            let missing = selectScope "diagnostic" names [ "chromium" ]

            Expect.throws
                (fun () -> expectedFiles root.FullName missing |> ignore)
                "Every unknown file rejects before fixture creation"

        let nested = Path.Combine(directory, "nested")
        Directory.CreateDirectory nested |> ignore
        File.WriteAllText(Path.Combine(nested, "synthetic-a.spec.ts"), "")

        for campaign in [ selected; batch ] do
            Expect.throws
                (fun () -> expectedFiles root.FullName campaign |> ignore)
                "Ambiguous basename rejects instead of selecting multiple files"

        Directory.Delete(nested, true)

        let arguments = browserArguments selected "chromium"
        let literal = @"(?:^|[/\\])synthetic-a\.spec\.ts$"

        Expect.equal
            arguments
            [
                "exec"
                "--"
                "playwright"
                "test"
                literal
                "--project"
                "chromium"
                "--forbid-only"
                "--max-failures=0"
                "--global-timeout=1200000"
            ]
            "Only the escaped known file is added; hard runner options stay locked"

        Expect.isTrue
            (Regex.IsMatch("/repo/web/e2e/synthetic-a.spec.ts", literal))
            "Literal selector reaches its file"

        Expect.isFalse
            (Regex.IsMatch("/repo/web/e2e/synthetic-aXspecYts", literal))
            "Dots are not arbitrary regex wildcards"

        Expect.isFalse
            (Regex.IsMatch("/repo/web/e2e/other-synthetic-a.spec.ts", literal))
            "Suffixes cannot select another basename"

        let node = System.Text.Json.Nodes.JsonNode.Parse(completionFixture false) |> nonNull
        let config = node["config"] |> nonNull
        config["rootDir"] <- System.Text.Json.Nodes.JsonValue.Create directory
        let broad = node.ToJsonString() |> Encoding.UTF8.GetBytes

        Expect.equal
            (BrowserCompletion.inspect batchFiles "chromium" broad)
            (2, "1.2s")
            "Every selected file must finish successfully"

        for before, after in
            [
                "synthetic-b.spec.ts", "extra.spec.ts"
                "\"status\":\"passed\"", "\"status\":\"skipped\""
                "\"retry\":0", "\"retry\":1"
            ] do
            let changed = node.ToJsonString().Replace(before, after, StringComparison.Ordinal)
            Expect.notEqual changed (node.ToJsonString()) "Negative mutates the intended report"

            Expect.throws
                (fun () ->
                    BrowserCompletion.inspect
                        batchFiles
                        "chromium"
                        (Encoding.UTF8.GetBytes changed)
                    |> ignore)
                "Missing, foreign, skipped and retried selected files never pass"

        Expect.throws
            (fun () -> BrowserCompletion.inspect files "chromium" broad |> ignore)
            "Unexpected extra executed file cannot qualify a diagnostic"

        let suites = (node["suites"] |> nonNull).AsArray()
        let specs = ((suites[0] |> nonNull)["specs"] |> nonNull).AsArray()
        specs.RemoveAt 1
        (node["stats"] |> nonNull)["expected"] <- System.Text.Json.Nodes.JsonValue.Create 1

        Expect.throws
            (fun () ->
                BrowserCompletion.inspect
                    batchFiles
                    "chromium"
                    (Encoding.UTF8.GetBytes(node.ToJsonString()))
                |> ignore)
            "A missing selected file is not a partial diagnostic success"

        Expect.equal
            (BrowserCompletion.inspect
                files
                "chromium"
                (Encoding.UTF8.GetBytes(node.ToJsonString())))
            (1, "1.2s")
            "A diagnostic still requires complete nonempty execution of its selected file"

        let output = Path.Combine(root.FullName, "output")
        let reports = Path.Combine(root.FullName, "reports")
        Directory.CreateDirectory output |> ignore
        File.WriteAllText(Path.Combine(output, "playwright.json"), node.ToJsonString())

        let previousReports =
            Environment.GetEnvironmentVariable "CLAIMCORE_TEST_REPORT_DIRECTORY"

        Environment.SetEnvironmentVariable("CLAIMCORE_TEST_REPORT_DIRECTORY", reports)

        try
            Environment.SetEnvironmentVariable("CLAIMCORE_TEST_REPORT_DIRECTORY", null)
            let fallback = reportDirectory ()
            Expect.equal (reportDirectory ()) fallback "Default evidence destination stays stable"
            report selected files output "chromium"

            Expect.isTrue
                (File.Exists(Path.Combine(fallback, "browser-chromium.json")))
                "Direct diagnostics retain evidence without caller environment setup"

            Directory.Delete(fallback, true)
            Environment.SetEnvironmentVariable("CLAIMCORE_TEST_REPORT_DIRECTORY", reports)
            report selected files output "chromium"

            use summary =
                JsonDocument.Parse(
                    File.ReadAllBytes(Path.Combine(reports, "browser-chromium.json"))
                )

            Expect.equal
                (summary.RootElement.GetProperty("scope").GetString())
                "diagnostic"
                "Partial execution is clearly labeled"

            Expect.isFalse
                (summary.RootElement.GetProperty("fullQualification").GetBoolean())
                "A passing diagnostic never claims qualification"

            let recorded =
                summary.RootElement.GetProperty("specifications").EnumerateArray()
                |> Seq.exactlyOne

            Expect.equal
                (recorded.GetString())
                "synthetic-a.spec.ts"
                "The selected file is retained in evidence"

            let workFile = Path.Combine(directory, "case-work.spec.ts")
            File.WriteAllText(workFile, "")
            let workScope = selectScope "diagnostic" "case-work.spec.ts" [ "chromium" ]
            let workNode = node.DeepClone()
            let workSuites = (workNode["suites"] |> nonNull).AsArray()
            let workSpecs = ((workSuites[0] |> nonNull)["specs"] |> nonNull).AsArray()

            (workSpecs[0] |> nonNull)["file"] <-
                System.Text.Json.Nodes.JsonValue.Create "case-work.spec.ts"

            File.WriteAllText(Path.Combine(output, "playwright.json"), workNode.ToJsonString())

            Expect.throws
                (fun () ->
                    report workScope (expectedFiles root.FullName workScope) output "chromium")
                "Selected work specification cannot pass without its required measurement artifact"
        finally
            Environment.SetEnvironmentVariable("CLAIMCORE_TEST_REPORT_DIRECTORY", previousReports)

        for campaign in
            [
                BrowserScope.Complete
                BrowserScope.Smoke
                BrowserScope.Diagnostic [ "unicode-parity.spec.ts" ]
            ] do
            Expect.isTrue
                (requiresSpecification campaign "unicode-parity.spec.ts")
                "Selected Unicode scope keeps native readback mandatory"

        Expect.isFalse
            (requiresSpecification selected "unicode-parity.spec.ts")
            "Unrelated file does not fabricate Unicode readback coverage"

        Expect.isTrue
            (requiresSpecification BrowserScope.Complete "case-work.spec.ts")
            "Complete keeps required work measurement"

        Expect.isTrue
            (requiresSpecification
                (BrowserScope.Diagnostic [ "case-work.spec.ts" ])
                "case-work.spec.ts")
            "Selected work specification keeps its artifact guard"
    finally
        root.Delete true

let private printFailureSummary root engine bytes =
    let codeFiles =
        try
            Directory.EnumerateFiles(
                Path.Combine(root, "web", "e2e"),
                "*",
                SearchOption.AllDirectories
            )
            |> Seq.toArray
        with
        | :? IOException
        | :? UnauthorizedAccessException -> [||]

    let passed, failed = BrowserProgress.counts bytes
    let locations = BrowserProgress.failedLocations codeFiles bytes |> String.concat ","
    let durations = BrowserProgress.failedDurations codeFiles bytes |> String.concat ","
    let stack = BrowserProgress.stackLocations codeFiles bytes |> String.concat ","
    let categories = BrowserProgress.failureCategories bytes |> String.concat ","
    let targets = BrowserProgress.targetGeometry bytes |> String.concat ","

    let summary =
        $"Published {engine}: completed rows passed={passed}, failed={failed}; categories={categories}; locations={locations}; durations={durations}; stack={stack}; targets={targets}"

    Console.WriteLine summary

    let output = Path.Combine(reportDirectory (), "browser-failure")
    Directory.CreateDirectory output |> ignore
    FixtureFiles.text output (engine + ".txt") summary |> ignore

let private runBrowser selected root engine output start =
    try
        // Leave one minute to report and clean up after the scope's Playwright bound.
        PublishedProcesses.run (processTimeout selected) start None
    with :? ClaimCore.TestSupport.BoundedProcessFailure as failure ->
        FixtureFiles.write output "playwright.stdout" failure.StandardOutput |> ignore
        FixtureFiles.write output "playwright.stderr" failure.StandardError |> ignore
        printFailureSummary root engine failure.StandardOutput

        Console.WriteLine(
            $"Published {engine}: bounded process cleanupFailed={failure.CleanupFailed}"
        )

        failtest ($"Browser process failure: {failure.Kind}; bounded private evidence retained.")

let private qualifyEngine engine =
    task {
        let root = ClaimCore.TestSupport.RepositoryRoot.find ()
        // Resolve the exact inventory before any expensive fixture or authentication starts.
        let files = expectedFiles root scope
        // Refuse an unusable evidence destination before starting the expensive fixture.
        Directory.CreateDirectory(reportDirectory ()) |> ignore

        Console.WriteLine(
            $"Starting published {engine} {scopeDescription scope}; isolated fixture, one worker, zero retries."
        )

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

        let start = PublishedProcesses.start "npm" (browserArguments scope engine) values

        start.WorkingDirectory <- Path.Combine(fixture.Root, "web")
        let result = runBrowser scope fixture.Root engine output start
        FixtureFiles.write output "playwright.stdout" result.StandardOutput |> ignore
        FixtureFiles.write output "playwright.stderr" result.StandardError |> ignore

        if result.ExitCode <> 0 then
            printFailureSummary fixture.Root engine result.StandardOutput

            BrowserLayoutReports.retain output (reportDirectory ()) engine

        Expect.equal
            result.ExitCode
            0
            ("Published browser scope " + engine + "; private evidence retained")

        report scope files output engine

        if requiresSpecification scope "unicode-parity.spec.ts" then
            BrowserReadback.qualify fixture output

        fixture.Complete()
    }

[<Tests>]
let tests =
    testSequenced
    <| testList
        (match scope with
         | BrowserScope.Complete -> "published browser qualification"
         | BrowserScope.Smoke -> "published browser smoke"
         | BrowserScope.Diagnostic _ -> "published browser diagnostic")
        [
            testCase
                "completion evidence rejects missing files, incomplete and retried executions"
                (fun () ->
                    completionControls ()
                    selectionControls ()
                    orchestrationControls ()
                    diagnosticControls ())
            testCaseAsync
                ((match scope with
                  | BrowserScope.Complete -> "complete browser specification suites on "
                  | BrowserScope.Smoke -> "workflow, localization and Unicode smoke on "
                  | BrowserScope.Diagnostic names ->
                      "diagnostic " + String.concat ", " names + " on ")
                 + String.concat ", " engines)
                (async { do! runSelected qualifyEngine engines |> Async.AwaitTask })
        ]
