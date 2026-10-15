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

let private start directory platform engine =
    let commands = Path.Combine(directory, "commands")
    File.WriteAllText(commands, "")
    executable directory "npm" "printf '%s\\n' \"$*\" >> \"$CLAIMCORE_TEST_COMMANDS_FILE\"\n"
    executable directory "uname" "printf '%s\\n' \"$CLAIMCORE_TEST_PLATFORM\"\n"

    let config =
        ProcessStartInfo(
            "bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    config.ArgumentList.Add(Path.Combine(RepositoryRoot.find (), "eng/verify.sh"))
    config.ArgumentList.Add("browsers")
    config.Environment["PATH"] <- directory + string Path.PathSeparator + config.Environment["PATH"]
    config.Environment["CLAIMCORE_TEST_COMMANDS_FILE"] <- commands
    config.Environment["CLAIMCORE_TEST_PLATFORM"] <- platform
    config.Environment.Remove("CLAIMCORE_ACCEPTANCE_BROWSER_ENGINE") |> ignore

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
            let config, commands = start directory platform engine
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
    for selection in [ "unlisted"; "" ] do
        let config, commands = start directory "Linux" (Some selection)
        let result = BoundedProcess.run config None 8192 10000
        Expect.equal result.ExitCode 64 "Invalid engine refuses"
        Expect.isEmpty (File.ReadAllLines commands) "No install starts for invalid selection"

[<Tests>]
let tests =
    testCase "browser installation covers selected specs and shared CLI authentication" (fun () ->
        let directory =
            Directory.CreateTempSubdirectory("claimcore-browser-install-").FullName

        try
            installSets directory
            invalidSelection directory
        finally
            Directory.Delete(directory, true))
