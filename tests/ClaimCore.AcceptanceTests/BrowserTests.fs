module ClaimCore.AcceptanceTests.BrowserTests

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Domain

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

let rec private specifications (suite: JsonElement) =
    seq {
        let mutable specs = Unchecked.defaultof<JsonElement>

        if suite.TryGetProperty("specs", &specs) then
            yield! specs.EnumerateArray()

        let mutable children = Unchecked.defaultof<JsonElement>

        if suite.TryGetProperty("suites", &children) then
            for child in children.EnumerateArray() do
                yield! specifications child
    }

let private failedSources output =
    try
        use document =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "playwright.json")))

        specifications document.RootElement
        |> Seq.filter (fun spec -> not (spec.GetProperty("ok").GetBoolean()))
        |> Seq.map (fun spec ->
            let definition = sourceLocation spec |> Option.defaultValue "unknown source"
            let failure = failedResults spec
            definition + (if failure = "" then "" else " at " + failure))
        |> Seq.distinct
        |> String.concat ", "
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
            || name = "trusted-types-auth-setup.json"
            || name = "trusted-types-violations.json"
            || name = "trusted-types-worker.json"
            || (name.StartsWith("test-failed-", StringComparison.Ordinal)
                && name.EndsWith(".png", StringComparison.Ordinal))
        then
            let target = Path.Combine(destination, Path.GetRelativePath(output, source))
            Directory.CreateDirectory(Path.GetDirectoryName target |> nonNull) |> ignore
            File.Copy(source, target, true)

let private includesUnicode report =
    specifications report
    |> Seq.exists (fun spec ->
        spec.GetProperty("file").GetString()
        |> nonNull
        |> fun file -> file.Contains("unicode-parity"))

let private requirePassingReport (report: JsonElement) =
    let stats = report.GetProperty("stats")
    let count (field: string) = stats.GetProperty(field).GetInt32()
    let expected = count "expected"
    Expect.isGreaterThan expected 0 "Selected browser scope is nonempty"
    Expect.equal (count "skipped") 0 "Selected browser scope contains no skipped checks"
    Expect.equal (count "unexpected") 0 "Every selected browser check passed"
    Expect.equal (count "flaky") 0 "Browser evidence does not hide a failed attempt"

    let occurrences =
        specifications report
        |> Seq.collect (fun spec ->
            let location = sourceLocation spec |> Option.defaultValue "unknown source"

            spec.GetProperty("tests").EnumerateArray()
            |> Seq.map (fun test -> location, test))
        |> Seq.toArray

    Expect.equal occurrences.Length expected "The report accounts for every selected browser check"

    for location, test in occurrences do
        Expect.equal
            (test.GetProperty("expectedStatus").GetString())
            "passed"
            (location + " must be expected to pass")

        let results = test.GetProperty("results")
        Expect.equal (results.GetArrayLength()) 1 (location + " must finish in one attempt")

        Expect.equal
            (results[0].GetProperty("status").GetString())
            "passed"
            (location + " must pass")

    expected

let private reportSpecTimings (report: JsonElement) =
    let timings =
        seq {
            for spec in specifications report do
                let name = spec.GetProperty("file").GetString() |> nonNull |> Path.GetFileName

                for test in spec.GetProperty("tests").EnumerateArray() do
                    let result = test.GetProperty("results")[0]
                    yield name, result.GetProperty("duration").GetDouble()
        }

    for name, duration in
        timings
        |> Seq.groupBy fst
        |> Seq.map (fun (name, cases) -> name, Seq.sumBy snd cases)
        |> Seq.sortBy (fun (name, duration) -> -duration, name)
        |> Seq.truncate 5 do
        printfn "Browser spec %s: %.0f ms across selected tests." name duration


let private draftSpec = "draft-editability.spec.ts"

let private browserShard =
    Environment.GetEnvironmentVariable "CLAIMCORE_ACCEPTANCE_BROWSER_SHARD"
    |> Option.ofObj

let private run engine policy =
    task {
        use fixture = new PublishedFixture.Fixture(referencePolicy = policy)
        let systemOnly = policy = CaseReferencePolicy.SystemAssignedOnly
        let spec = if systemOnly then Some draftSpec else specification

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

        if systemOnly then
            let registrar =
                {|
                    kind = "HUMAN"
                    issuer = fixture.Issuer
                    subject = ClaimCore.TestSupport.IdentityRealm.ownerSubject
                |}

            CliCalls.grant fixture registrar (Role.token Role.CaseRegistrar)
            CliCalls.setGrant fixture registrar (Role.token Role.CaseHandler) false

        let output = Path.Combine(fixture.Directory, engine)
        Directory.CreateDirectory output |> ignore
        let values = Dictionary<string, string>()
        values["CLAIMCORE_WEB_BASE_URL"] <- fixture.Origin
        values["CLAIMCORE_WEB_E2E_BACKEND_URL"] <- fixture.Backend
        values["CLAIMCORE_WEB_E2E_ENGINE"] <- engine
        values["CLAIMCORE_WEB_E2E_REFERENCE_POLICY"] <- CaseReferencePolicy.token policy
        values["CLAIMCORE_WEB_E2E_REDIRECT_STATE_DIR"] <- fixture.Directory
        values["CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR"] <- output

        values["CLAIMCORE_TEST_OIDC_CREDENTIALS"] <-
            Path.Combine(fixture.Directory, "credentials.json")

        values["CLAIMCORE_WEB_E2E_PRINCIPALS_FILE"] <-
            Path.Combine(fixture.Directory, "principals.json")

        let arguments =
            [ "exec"; "--"; "playwright"; "test" ]
            @ (spec |> Option.toList)
            @ (if engine = "chromium" && not systemOnly then
                   [ "--project"; engine; "--project"; "chromium-classic" ]
               else
                   [ "--project"; engine ])
            @ (browserShard
               |> Option.filter (fun _ -> engine = "chromium" && not systemOnly)
               |> Option.map (fun shard -> [ "--shard"; shard ])
               |> Option.defaultValue [])
            @ (if systemOnly then [ "--grep"; "automatic opening" ]
               elif engine = "chromium" then []
               else [ "--grep"; "@engine" ])

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

        use report =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "playwright.json")))

        let passed =
            try
                requirePassingReport report.RootElement
            with _ ->
                failureReport |> Option.iter (retainFailureFiles output)
                reraise ()

        if systemOnly then
            Expect.equal passed 2 "Both system-only opening journeys must run"

        printfn
            "Published browser %s (%s): %d passed on the first attempt."
            engine
            (CaseReferencePolicy.token policy)
            passed

        reportSpecTimings report.RootElement

        if
            engine = "chromium"
            && (specification |> Option.forall (fun value -> value.Contains("unicode-parity")))
        then
            if includesUnicode report.RootElement then
                BrowserReadback.qualify fixture output

        fixture.Complete()
    }

[<Tests>]
let reportTest =
    testCase "Playwright report rejects expected failures and incomplete results" (fun () ->
        let check expectedStatus results =
            use report =
                JsonDocument.Parse(
                    $$"""{"stats":{"expected":1,"skipped":0,"unexpected":0,"flaky":0},"suites":[{"specs":[{"file":"synthetic.spec.ts","line":1,"tests":[{"expectedStatus":"{{expectedStatus}}","results":{{results}}}]}]}]}"""
                )

            requirePassingReport report.RootElement

        let passed = """[{"status":"passed"}]"""
        let failed = """[{"status":"failed"}]"""
        Expect.equal (check "passed" passed) 1 "One passing attempt qualifies"
        Expect.throws (fun () -> check "failed" failed |> ignore) "An expected failure is refused"
        Expect.throws (fun () -> check "passed" failed |> ignore) "A failed attempt is refused"
        Expect.throws (fun () -> check "passed" "[]" |> ignore) "A missing attempt is refused")

[<Tests>]
let tests =
    testCaseAsync
        "published browser acceptance"
        (async {
            for engine in engines do
                do! run engine CaseReferencePolicy.CustomAllowed |> Async.AwaitTask

            // Qualify both policies once in the Chromium matrix, or when the opening spec is selected locally.
            if
                List.contains "chromium" engines
                && (browserShard
                    |> Option.forall (fun shard -> shard.StartsWith("1/", StringComparison.Ordinal)))
                && (specification
                    |> Option.forall (fun filter ->
                        Text.RegularExpressions.Regex.IsMatch("e2e/" + draftSpec, filter)))
            then
                do! run "chromium" CaseReferencePolicy.SystemAssignedOnly |> Async.AwaitTask
        })

[<Tests>]
let installationBindingTests =
    testTask "published CLI rejects a prepared termination from another fresh register" {
        let scope fixture =
            CliCalls.human fixture "synthetic-outsider" "context" {| |} 0
            |> CliCalls.require "SUCCEEDED"
            |> fun context -> context.GetProperty("terminationScope")

        let! original, prepared =
            task {
                use fixture = new PublishedFixture.Fixture()
                do! fixture.Start()

                let installation =
                    (scope fixture).GetProperty("installationId").GetString() |> nonNull

                let result =
                    CliCalls.invoke
                        fixture
                        fixture.Environment
                        [
                            "session"
                            "terminate-own"
                            "--installation-id"
                            installation
                            "--dry-run"
                        ]
                        Array.empty
                        0

                Expect.equal result.ExitCode 0 "Published preparation succeeds"

                let frame =
                    ClaimCore.Cli.Frames.admit result.StandardOutput
                    |> Result.toOption
                    |> Option.get

                Expect.equal
                    (ClaimCore.Cli.Frames.installationId frame)
                    (Some(Guid.Parse installation))
                    "Preparation binds original installation"

                fixture.Complete()
                return installation, result.StandardOutput
            }

        use fresh = new PublishedFixture.Fixture()
        do! fresh.Start()
        let before = scope fresh
        let installation = before.GetProperty("installationId").GetString() |> nonNull
        Expect.notEqual installation original "An independently initialized register was selected"

        let environment = CliCalls.interactiveEnvironment fresh "synthetic-outsider" "valid"

        let invoke bytes code =
            let result = CliCalls.invoke fresh environment [ "call" ] bytes code
            Expect.equal result.ExitCode code "Published CLI exit"

            ClaimCore.Cli.Frames.decode result.StandardOutput
            |> Result.toOption
            |> Option.get

        let refused = invoke prepared 3

        Expect.equal
            refused.Delivery
            ClaimCore.Cli.DeliveryState.NotSent
            "Wrong-register preparation never dispatches"

        match refused.Result with
        | ClaimCore.Cli.FrameResult.HostFailure failure ->
            Expect.equal
                failure.Code
                ClaimCore.Wire.HostFailureCode.InstallationMismatch
                "Original installation is retained"
        | _ -> failtest "Expected installation mismatch at the published CLI/service boundary."

        let raw install =
            let input =
                dict
                    [
                        "input:operationId", box (CliCalls.id ())
                        "input:confirmRetirement", box true
                    ]

            match install with
            | None -> CliCalls.frame "session.terminateOwn" input
            | Some binding ->
                JsonSerializer.SerializeToUtf8Bytes
                    {|
                        endpoint = "session.terminateOwn"
                        input = input
                        installationId = binding
                    |}

        for malformed in [ None; Some "invalid"; Some(Guid.Empty.ToString("D")) ] do
            let rejected = invoke (raw malformed) 2

            Expect.equal
                rejected.Delivery
                ClaimCore.Cli.DeliveryState.NotSent
                "Missing or invalid bound frames never dispatch"

            match rejected.Result with
            | ClaimCore.Cli.FrameResult.LocalFailure(ClaimCore.Cli.CliFailure.InvalidFrame _) -> ()
            | _ -> failtest "Expected a local frame refusal."

        let still = scope fresh

        Expect.equal
            (still.GetProperty("retirementCutoff").GetRawText())
            (before.GetProperty("retirementCutoff").GetRawText())
            "Misdelivery leaves the fresh register cutoff unchanged"

        let committed = CliCalls.run fresh environment (raw (Some installation)) 0

        Expect.equal
            (CliCalls.outcomeKind committed)
            "RETIREMENT_CONFIRMED"
            "A correctly bound retirement succeeds"

        let freshCommand = CliCalls.verb fresh environment [ "session"; "terminate-own" ] 0
        let count = CliCalls.require "SUCCEEDED" freshCommand

        Expect.isTrue
            (count.GetProperty("self").GetBoolean())
            "Fresh named CLI obtains typed unregistered scope"

        fresh.Complete()
    }
