module ClaimCore.AcceptanceTests.BrowserTests

open System
open System.Text
open System.Text.Json
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

let private smokeScope (value: string | null) =
    match value with
    | null
    | "complete" -> false
    | "smoke" -> true
    | _ -> invalidOp "Acceptance browser scope must be complete or smoke."

let private engines =
    Environment.GetEnvironmentVariable "CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE"
    |> selectEngines

let private smoke =
    let selected =
        Environment.GetEnvironmentVariable "CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE"
        |> smokeScope

    if selected && engines.Length <> 1 then
        invalidOp
            "Smoke requires one explicit acceptance browser engine; it is not complete qualification."

    selected

let private selectedSpecifications =
    if smoke then
        [
            "authority-uncertainty.spec.ts"
            "act-lifecycle.spec.ts"
            "localization-correction.spec.ts"
            "localization-editor.spec.ts"
            "localization-pseudolocale.spec.ts"
            "localization-session-history.spec.ts"
            "narrow-role.spec.ts"
            "recovery-journal.spec.ts"
            "resolution.spec.ts"
            "work-surface-foundations.spec.ts"
            "stewardship-authority.spec.ts"
            "stewardship-localization.spec.ts"
            "unicode-parity.spec.ts"
        ]
    else
        []

let private expectedFiles root =
    let directory = Path.Combine(root, "web", "e2e")

    (if smoke then
         selectedSpecifications |> Seq.map (fun file -> Path.Combine(directory, file))
     else
         Directory.EnumerateFiles(directory, "*.spec.ts", SearchOption.AllDirectories))
    |> Seq.map Path.GetFullPath
    |> Set.ofSeq

let private report root output engine =
    let count, duration =
        BrowserCompletion.read (expectedFiles root) engine (Path.Combine(output, "playwright.json"))

    let scope =
        if smoke then
            "smoke; full browser qualification remains unrun"
        else
            "complete engine suite"

    let directory =
        match Environment.GetEnvironmentVariable "CLAIMCORE_TEST_REPORT_DIRECTORY" with
        | null ->
            Path.Combine(
                ClaimCore.TestSupport.RepositoryRoot.find (),
                "artifacts",
                "verification",
                "native." + Guid.NewGuid().ToString("N")
            )
        | value -> value

    Directory.CreateDirectory directory |> ignore

    JsonSerializer.SerializeToUtf8Bytes
        {|
            runId = Path.GetFileName directory
            engine = engine
            scope = if smoke then "smoke" else "complete"
            passed = count
            duration = duration
        |}
    |> FixtureFiles.write directory ("browser-" + engine + ".json")
    |> ignore

    Console.WriteLine($"Published {engine} ({scope}): {count} passed; {duration}")

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
            (fun () -> smokeScope value |> ignore)
            "Invalid campaign cannot silently shorten work"

    Expect.isFalse (smokeScope null) "Default remains complete"
    Expect.isFalse (smokeScope "complete") "Complete scope is explicit"
    Expect.isTrue (smokeScope "smoke") "Smoke is explicitly selected"

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

let private browserArguments engine =
    [ "exec"; "--"; "playwright"; "test" ]
    @ selectedSpecifications
    @ [ "--project"; engine; "--forbid-only"; "--max-failures=1" ]
    @ (if smoke then [ "--global-timeout=120000" ] else [])

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

    match Environment.GetEnvironmentVariable "CLAIMCORE_TEST_REPORT_DIRECTORY" with
    | null -> ()
    | directory ->
        let output = Path.Combine(directory, "browser-failure")
        Directory.CreateDirectory output |> ignore
        FixtureFiles.text output (engine + ".txt") summary |> ignore

let private runBrowser root engine output start =
    try
        PublishedProcesses.run 900_000 start None
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
        let scope =
            if smoke then
                "smoke; complete qualification is not being run"
            else
                "complete engine suite"

        Console.WriteLine(
            $"Starting published {engine} {scope}; isolated fixture, one worker, zero retries."
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

        let start = PublishedProcesses.start "npm" (browserArguments engine) values

        start.WorkingDirectory <- Path.Combine(fixture.Root, "web")
        let result = runBrowser fixture.Root engine output start
        FixtureFiles.write output "playwright.stdout" result.StandardOutput |> ignore
        FixtureFiles.write output "playwright.stderr" result.StandardError |> ignore

        if result.ExitCode <> 0 then
            printFailureSummary fixture.Root engine result.StandardOutput

        Expect.equal
            result.ExitCode
            0
            ("Published browser scope " + engine + "; private evidence retained")

        report fixture.Root output engine
        BrowserReadback.qualify fixture output
        fixture.Complete()
    }

[<Tests>]
let tests =
    testSequenced
    <| testList
        (if smoke then
             "published browser smoke"
         else
             "published browser qualification")
        [
            testCase
                "completion evidence rejects missing files, incomplete and retried executions"
                (fun () ->
                    completionControls ()
                    selectionControls ()
                    orchestrationControls ())
            testCaseAsync
                ((if smoke then
                      "workflow, localization and Unicode smoke on "
                  else
                      "complete browser specification suites on ")
                 + String.concat ", " engines)
                (async { do! runSelected qualifyEngine engines |> Async.AwaitTask })
        ]
