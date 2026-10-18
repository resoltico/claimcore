module ClaimCore.Tests.VerificationStageTests

open System
open System.IO
open System.Diagnostics
open Expecto
open ClaimCore.TestSupport

let private executable directory name source =
    let path = Path.Combine(directory, name)
    File.WriteAllText(path, "#!/bin/sh\n" + source)

    File.SetUnixFileMode(
        path,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )

let private commandStubs directory =
    executable
        directory
        "npm"
        "printf '%s\\n' \"$*\" >> \"$CLAIMCORE_TEST_COMMANDS_FILE\"\nif [ \"${CLAIMCORE_TEST_FAIL_COMMAND:-}\" = \"$*\" ]; then exit 19; fi\n"

    executable
        directory
        "dotnet"
        "printf 'dotnet %s\\n' \"$*\" >> \"$CLAIMCORE_TEST_COMMANDS_FILE\"\nif [ \"${CLAIMCORE_TEST_FAIL_COMMAND:-}\" = \"dotnet $*\" ]; then exit 19; fi\n"

    executable
        directory
        "docker"
        "printf '%s\\n' \"$*\" >> \"$CLAIMCORE_TEST_COMMANDS_FILE.docker\"\nif [ \"${CLAIMCORE_TEST_FAIL_DOCKER:-}\" = true ]; then exit 19; fi\n"

    executable
        directory
        "shellcheck"
        "printf 'shellcheck %s\\n' \"$*\" >> \"$CLAIMCORE_TEST_COMMANDS_FILE\"\nif [ \"${CLAIMCORE_TEST_FAIL_COMMAND:-}\" = \"shellcheck $*\" ]; then exit 19; fi\n"

    executable directory "uname" "printf '%s\\n' \"$CLAIMCORE_TEST_PLATFORM\"\n"

let private start directory platform engine stages =
    let commands = Path.Combine(directory, "commands")
    File.WriteAllText(commands, "")
    File.WriteAllText(commands + ".docker", "")

    commandStubs directory

    let config =
        ProcessStartInfo(
            "bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    config.ArgumentList.Add(Path.Combine(RepositoryRoot.find (), "eng/verify.sh"))
    stages |> List.iter config.ArgumentList.Add
    config.Environment["PATH"] <- directory + string Path.PathSeparator + config.Environment["PATH"]
    config.Environment["CLAIMCORE_TEST_COMMANDS_FILE"] <- commands
    config.Environment["CLAIMCORE_TEST_PLATFORM"] <- platform

    for name in
        [
            "CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE"
            "CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE"
            "CLAIMCORE_TEST_FAIL_COMMAND"
            "CLAIMCORE_TEST_FAIL_DOCKER"
            "CLAIMCORE_TEST_POSTGRES_MAJOR"
        ] do
        config.Environment.Remove(name) |> ignore

    engine
    |> Option.iter (fun value -> config.Environment["CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE"] <- value)

    config, commands

let private stageArguments directory =
    for stages in
        [
            [ "--fast"; "acceptance" ]
            [ "--fast"; "not-a-stage" ]
            [ "--fast"; "--fast" ]
            [ "unit"; "--fast" ]
            [ "" ]
            [ ""; "acceptance" ]
        ] do
        let config, commands = start directory "Linux" None stages
        let result = BoundedProcess.run config None 8192 10000
        Expect.equal result.ExitCode 64 "Malformed selectors refuse before work"
        Expect.isEmpty (File.ReadAllLines commands) "No tool starts"
        Expect.isEmpty (File.ReadAllLines(commands + ".docker")) "No Docker starts"

let private orderedStages directory =
    for stages in [ [ "--fast" ]; [ "frontend"; "unit" ]; [] ] do
        let config, commands = start directory "Linux" None stages
        let result = BoundedProcess.run config None 8192 10000
        Expect.equal result.ExitCode 0 "Supported selectors succeed"
        let actual = File.ReadAllLines commands |> Array.toList

        let first (prefix: string) =
            actual |> List.findIndex _.StartsWith(prefix)

        if stages = [ "frontend"; "unit" ] then
            Expect.isLessThan (first "--prefix web ci") (first "dotnet restore") "Named order"
        else
            Expect.isLessThan
                (first "dotnet tool restore")
                (first "dotnet test ")
                "Lint before unit"

            Expect.isLessThan
                (first "dotnet test ")
                (first "--prefix web ci")
                "Unit before frontend"

        if stages = [ "--fast" ] then
            Expect.isEmpty (File.ReadAllLines(commands + ".docker")) "Fast needs no Docker"
        else if stages.IsEmpty then
            Expect.isTrue
                (actual |> List.exists _.StartsWith("shellcheck "))
                "Default workflow lint uses the fixture command"

            Expect.isTrue
                ((File.ReadAllLines(commands + ".docker")).Length > 0)
                "Default includes Docker"

let private installSets directory =
    let expected =
        [
            None, "chromium firefox webkit"
            Some "chromium", "chromium"
            Some "firefox", "chromium firefox"
            Some "webkit", "chromium webkit"
        ]

    for platform in [ "Linux"; "Darwin" ] do
        for engine, engines in expected do
            let config, commands = start directory platform engine [ "browsers" ]
            let result = BoundedProcess.run config None 8192 10000
            Expect.equal result.ExitCode 0 "Browser prerequisite stage succeeds"
            let dependencies = if platform = "Linux" then "--with-deps " else ""

            Expect.equal
                (File.ReadAllLines commands |> Array.toList)
                [
                    "--prefix web ci"
                    "--prefix web exec -- playwright install " + dependencies + engines
                ]
                "Selected engine plus shared Chromium auth prerequisite, without duplication"

let private invalidSelection directory =
    for engine, scope in
        [
            Some "unlisted", None
            Some "", None
            None, Some "smoke"
            Some "chromium", Some ""
            Some "chromium", Some "unlisted"
        ] do
        for stages in [ [ "browsers" ]; [ "unit"; "acceptance" ] ] do
            let config, commands = start directory "Linux" engine stages

            scope
            |> Option.iter (fun value ->
                config.Environment["CLAIMCORE_ACCEPTANCE_BROWSER_SCOPE"] <- value)

            let result = BoundedProcess.run config None 8192 10000
            Expect.equal result.ExitCode 64 "Invalid selection refuses before any selected stage"
            Expect.isEmpty (File.ReadAllLines commands) "No tool starts for invalid selection"
            Expect.isEmpty (File.ReadAllLines(commands + ".docker")) "No Docker preflight starts"

let private invalidPostgresSelection directory =
    for major in [ ""; "0"; "018"; "text"; "18 "; "99999" ] do
        for stages in [ [ "postgres" ]; [ "unit"; "integration" ]; [ "acceptance" ] ] do
            let config, commands = start directory "Linux" None stages
            config.Environment["CLAIMCORE_TEST_POSTGRES_MAJOR"] <- major
            let result = BoundedProcess.run config None 8192 10000
            Expect.equal result.ExitCode 64 "Unlisted major refuses before any stage"
            Expect.isEmpty (File.ReadAllLines commands) "No build, restore or test starts"
            Expect.isEmpty (File.ReadAllLines(commands + ".docker")) "No Docker preflight starts"

let private listedPostgresSelection directory =
    let baseline = Path.Combine(RepositoryRoot.find (), "db/postgresql-baseline.json")
    use document = System.Text.Json.JsonDocument.Parse(File.ReadAllText baseline)

    for entry in document.RootElement.GetProperty("supportedMajors").EnumerateArray() do
        let config, commands = start directory "Linux" None [ "postgres" ]

        config.Environment["CLAIMCORE_TEST_POSTGRES_MAJOR"] <-
            string (entry.GetProperty("major").GetInt32())

        let result = BoundedProcess.run config None 8192 10000
        Expect.equal result.ExitCode 0 "Every declared major passes shell preflight"
        Expect.isNonEmpty (File.ReadAllLines commands) "Selected suite launches with stubbed tools"

let private sharedBuild directory =
    for stages in [ [ "frontend"; "acceptance" ]; [ "acceptance" ] ] do
        let config, commands = start directory "Linux" None stages
        let result = BoundedProcess.run config None 8192 10000
        Expect.equal result.ExitCode 0 "Stages succeed with stubbed tools"
        let actual = File.ReadAllLines commands

        Expect.equal
            (actual |> Array.filter ((=) "--prefix web run build") |> Array.length)
            1
            "Assets build once"

        Expect.equal
            (actual |> Array.filter ((=) "--prefix web ci") |> Array.length)
            1
            "Packages restore once"

        let execution = actual |> Array.find _.StartsWith("dotnet test ")

        Expect.stringContains
            execution
            "ClaimCore.AcceptanceTests.fsproj"
            "Acceptance launches after assets"

        Expect.stringContains
            execution
            "--minimum-expected-tests=19"
            "Independent acceptance discovery floor"

let private failedBuild directory =
    let config, commands = start directory "Linux" None [ "acceptance" ]
    config.Environment["CLAIMCORE_TEST_FAIL_COMMAND"] <- "--prefix web run build"
    let result = BoundedProcess.run config None 8192 10000
    Expect.equal result.ExitCode 19 "Asset failure remains visible"

    Expect.isFalse
        (File.ReadAllLines commands |> Array.exists _.StartsWith("dotnet test "))
        "Acceptance tests never launch after a failed asset build"

let private nativeStages directory =
    let project suite =
        "tests/ClaimCore." + suite + "Tests/ClaimCore." + suite + "Tests.fsproj"

    for stage, suite, floor in [ "postgres", "Integration", 123; "web", "Web", 76 ] do
        let config, commands = start directory "Linux" None [ stage ]
        let result = BoundedProcess.run config None 8192 10000
        Expect.equal result.ExitCode 0 "Independent native suite succeeds"
        let actual = File.ReadAllLines commands

        let build =
            "dotnet build " + project suite + " --configuration Release --no-restore"

        Expect.contains actual build "Only the selected project closure builds"

        Expect.contains
            actual
            ("dotnet restore " + project suite + " --locked-mode")
            "Only the selected project closure restores"

        Expect.equal
            (actual |> Array.filter _.StartsWith("dotnet build ") |> Array.length)
            1
            "One build"

        let execution = actual |> Array.find _.StartsWith("dotnet test ")

        Expect.stringContains
            execution
            ("--minimum-expected-tests=" + string floor)
            "Complete suite floor"

        let failed, failedCommands = start directory "Linux" None [ stage ]
        failed.Environment["CLAIMCORE_TEST_FAIL_COMMAND"] <- build
        let failure = BoundedProcess.run failed None 8192 10000
        Expect.equal failure.ExitCode 19 "Build failure remains visible"

        Expect.isFalse
            (File.ReadAllLines failedCommands |> Array.exists _.StartsWith("dotnet test "))
            "No suite executes after a failed build"

let private failedNativeRestore directory =
    let config, commands = start directory "Linux" None [ "postgres" ]

    config.Environment["CLAIMCORE_TEST_FAIL_COMMAND"] <-
        "dotnet restore tests/ClaimCore.IntegrationTests/ClaimCore.IntegrationTests.fsproj --locked-mode"

    let result = BoundedProcess.run config None 8192 10000
    Expect.equal result.ExitCode 19 "Restore failure remains visible"

    Expect.equal
        (File.ReadAllLines commands).Length
        1
        "No build or test starts after restore failure"

let private combinedNativeStages directory =
    let config, commands = start directory "Linux" None [ "integration" ]
    let result = BoundedProcess.run config None 8192 10000
    Expect.equal result.ExitCode 0 "Combined integration succeeds"
    let actual = File.ReadAllLines commands

    Expect.equal
        (actual |> Array.filter _.StartsWith("dotnet build ") |> Array.length)
        2
        "Only PostgreSQL and Web project closures build"

    Expect.equal
        (actual |> Array.filter _.StartsWith("dotnet restore ") |> Array.toList)
        [
            "dotnet restore tests/ClaimCore.IntegrationTests/ClaimCore.IntegrationTests.fsproj --locked-mode"
            "dotnet restore tests/ClaimCore.WebTests/ClaimCore.WebTests.fsproj --locked-mode"
        ]
        "Only selected project closures restore"

    Expect.equal (File.ReadAllLines(commands + ".docker")).Length 1 "Daemon preflight is shared"

    let suites = actual |> Array.filter _.StartsWith("dotnet test ")
    Expect.equal suites.Length 2 "Both complete suites run"
    Expect.stringContains suites[0] "ClaimCore.IntegrationTests.fsproj" "PostgreSQL runs first"

    Expect.stringContains
        suites[0]
        "eng/expecto.integration.runsettings"
        "Integration concurrency is scoped"

    Expect.stringContains suites[1] "ClaimCore.WebTests.fsproj" "Web runs second"
    Expect.stringContains suites[1] "eng/expecto.runsettings" "Web settings remain unchanged"

let private nativeUnitFloor directory =
    let config, commands = start directory "Linux" None [ "unit" ]
    let result = BoundedProcess.run config None 8192 10000
    Expect.equal result.ExitCode 0 "Unit stage launches complete native qualification"

    let executions =
        File.ReadAllLines commands |> Array.filter _.StartsWith("dotnet test ")

    Expect.equal executions.Length 2 "Both complete native suites launch"

    for execution, floor in List.zip (Array.toList executions) [ 342; 43 ] do
        Expect.stringContains
            execution
            ("--minimum-expected-tests=" + string floor)
            "Independent native and architecture discovery floors"

let private unavailableDocker directory =
    for stages in
        [
            [ "unit"; "postgres" ]
            [ "browsers"; "acceptance" ]
            [ "workflows" ]
            [ "web" ]
        ] do
        let config, commands = start directory "Linux" None stages
        config.Environment["CLAIMCORE_TEST_FAIL_DOCKER"] <- "true"
        let result = BoundedProcess.run config None 8192 10000
        Expect.equal result.ExitCode 69 "Missing Docker fails before expensive work"
        Expect.isEmpty (File.ReadAllLines commands) "No restore, build or browser download starts"

        Expect.equal
            (File.ReadAllLines(commands + ".docker"))
            [| "info --format {{.ServerVersion}}" |]
            "One daemon preflight"

[<Tests>]
let tests =
    testCase
        "verification stages preserve prerequisites, early refusals and shared builds"
        (fun () ->
            let directory =
                Directory.CreateTempSubdirectory("claimcore-verification-stage-").FullName

            try
                stageArguments directory
                orderedStages directory
                installSets directory
                invalidSelection directory
                invalidPostgresSelection directory
                listedPostgresSelection directory
                sharedBuild directory
                failedBuild directory
                nativeStages directory
                failedNativeRestore directory
                combinedNativeStages directory
                unavailableDocker directory
                nativeUnitFloor directory
            finally
                Directory.Delete(directory, true))
