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

let private start directory platform engine stages =
    let commands = Path.Combine(directory, "commands")
    File.WriteAllText(commands, "")

    executable
        directory
        "npm"
        "printf '%s\\n' \"$*\" >> \"$CLAIMCORE_TEST_COMMANDS_FILE\"\nif [ \"${CLAIMCORE_TEST_FAIL_COMMAND:-}\" = \"$*\" ]; then exit 19; fi\n"

    executable
        directory
        "dotnet"
        "printf 'dotnet %s\\n' \"$*\" >> \"$CLAIMCORE_TEST_COMMANDS_FILE\"\n"

    executable directory "uname" "printf '%s\\n' \"$CLAIMCORE_TEST_PLATFORM\"\n"

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
        ] do
        config.Environment.Remove(name) |> ignore

    engine
    |> Option.iter (fun value -> config.Environment["CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE"] <- value)

    config, commands

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

        Expect.isTrue
            (actual |> Array.exists _.StartsWith("dotnet test "))
            "Acceptance launches after assets build"

let private failedBuild directory =
    let config, commands = start directory "Linux" None [ "acceptance" ]
    config.Environment["CLAIMCORE_TEST_FAIL_COMMAND"] <- "--prefix web run build"
    let result = BoundedProcess.run config None 8192 10000
    Expect.equal result.ExitCode 19 "Asset failure remains visible"

    Expect.isFalse
        (File.ReadAllLines commands |> Array.exists _.StartsWith("dotnet test "))
        "Acceptance tests never launch after a failed asset build"

[<Tests>]
let tests =
    testCase
        "verification stages preserve prerequisites, early refusals and shared builds"
        (fun () ->
            let directory =
                Directory.CreateTempSubdirectory("claimcore-verification-stage-").FullName

            try
                installSets directory
                invalidSelection directory
                sharedBuild directory
                failedBuild directory
            finally
                Directory.Delete(directory, true))
