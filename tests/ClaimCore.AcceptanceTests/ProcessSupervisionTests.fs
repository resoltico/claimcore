module ClaimCore.AcceptanceTests.ProcessSupervisionTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading.Tasks
open Expecto
open ClaimCore.TestSupport

let private start script =
    let value =
        ProcessStartInfo(
            "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    value.ArgumentList.Add "-c"
    value.ArgumentList.Add script
    value

let private completed (work: Task<'value>) =
    work.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()

let private refused (work: Task<int>) message =
    Expect.throwsC (fun () -> completed work |> ignore) (fun error ->
        Expect.equal
            (error.GetType())
            typeof<InvalidOperationException>
            "Status refusal is not a deadline"

        Expect.equal error.Message message "Status diagnostics contain only fixed safe text")

let private protocolControls () =
    for code in [ 0; 7; 127; 255 ] do
        Expect.equal (ProcessSupervision.parseTargetStatus $"E {code}") code "Exact normal exit"

    Expect.equal (ProcessSupervision.parseTargetStatus "K 15") 143 "Runtime signal exit mapping"

    for value in [ ""; "E -1"; "E 256"; "E +7"; "E 7 "; "K 0"; "K 128"; "F"; "private" ] do
        Expect.throws
            (fun () -> ProcessSupervision.parseTargetStatus value |> ignore)
            "Malformed or failed-start status is refused"

let private earlyTeardown () =
    use bytes = new MemoryStream(Encoding.UTF8.GetBytes "T\n")

    let target =
        TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    ProcessSupervision.readStatus bytes target |> completed
    refused target.Task "Test process target was stopped."

let private exitedLeader () =
    let command = start "sleep 30 >/dev/null 2>&1 & printf '%s\n' \"$!\"; exit 7"
    use owned = new ProcessSupervision.Session(ProcessSupervision.executable, command)

    let childId =
        completed (owned.Process.StandardOutput.ReadLineAsync())
        |> nonNull
        |> Int32.Parse

    use descendant = Process.GetProcessById childId
    Expect.equal (completed owned.Target) 7 "Target status does not become supervisor status"
    Expect.isFalse descendant.HasExited "Ordinary descendant remains while its anchor is retained"
    Expect.isTrue (owned.Stop()) "Retained anchor tears down its inherited-group descendant"
    descendant.Refresh()

    Expect.isTrue
        descendant.HasExited
        "Owned descendant has exited, rather than merely losing its pipes"

let private completedLauncher () =
    let command = start "sleep 2 >/dev/null 2>&1 & printf '%s\n' \"$!\"; exit 0"
    let result = BoundedProcess.run command None 64 2000
    let childId = Encoding.UTF8.GetString(result.StandardOutput).Trim() |> Int32.Parse

    let descendant =
        try
            Some(Process.GetProcessById childId)
        with :? ArgumentException ->
            None

    match descendant with
    | None -> ()
    | Some child ->
        use owned = child

        try
            Expect.isTrue
                child.HasExited
                "A completed BoundedProcess result includes ordinary descendant cleanup"
        finally
            Expect.isTrue
                (child.WaitForExit(3000))
                "Finite owned fixture leaves no surviving descendant even when a regression rejects"

let private controllerEof () =
    use owned =
        new ProcessSupervision.Session(
            ProcessSupervision.executable,
            start "printf 'ready\n'; exec sleep 30"
        )

    Expect.equal
        (completed (owned.Process.StandardOutput.ReadLineAsync()))
        "ready"
        "Measured target readiness precedes controller EOF"

    owned.CloseControl()
    Expect.isTrue (owned.Process.WaitForExit(2000)) "EOF tears down without a parent group signal"
    Expect.isTrue (owned.Stop()) "Protocol and observation confirm completed EOF cleanup"

let private statusPipeFailure () =
    use owned =
        new ProcessSupervision.Session(
            ProcessSupervision.executable,
            start "printf 'ready\n'; exec sleep 30"
        )

    Expect.equal
        (completed (owned.Process.StandardOutput.ReadLineAsync()))
        "ready"
        "Target is executing before status loss"

    owned.CloseStatus()
    owned.CloseControl()

    Expect.isTrue
        (owned.Process.WaitForExit(2000))
        "A broken status pipe cannot kill the anchor before teardown"

    Expect.isFalse (owned.Stop()) "Missing confirmation remains an explicit cleanup failure"

    Expect.isTrue
        (ProcessSupervision.observe ProcessSupervision.executable owned.Process.Id 2000)
        "Observation confirms the owned group is gone without signaling"

let private signalRestoration () =
    for signal in [ "TERM"; "PIPE" ] do
        let script = "kill -" + signal + " $$"
        use direct = new Process(StartInfo = start script)
        Expect.isTrue (direct.Start()) "Reference target starts under the same runtime"
        Expect.isTrue (direct.WaitForExit(2000)) "Quiet reference target completes"
        let result = BoundedProcess.run (start script) None 64 2000

        Expect.equal
            result.ExitCode
            direct.ExitCode
            "Exec targets restore their intended inherited signal dispositions"

        Expect.isEmpty result.StandardOutput "Signal target stdout"
        Expect.isEmpty result.StandardError "Signal target stderr"

let private refusalControls directory =
    let command = start "exit 0"

    Expect.throwsC
        (fun () ->
            use _owned =
                new ProcessSupervision.Session(Path.Combine(directory, "absent"), command)

            ())
        (fun error ->
            Expect.equal
                error.Message
                "Test process supervisor is unavailable or unsupported."
                "Missing helper has a safe diagnostic")

    for body, message in
        [
            "exit 0", "Incomplete test process status."
            "printf 'private-malformed-status\\n' > /dev/fd/\"$2\"", "Invalid test process status."
        ] do
        let helper = Path.Combine(directory, "protocol-fixture")
        let native = "'" + ProcessSupervision.executable.Replace("'", "'\"'\"'") + "'"

        let dispatch =
            "case \"$1\" in --resolve|--observe) exec " + native + " \"$@\" ;; esac\n"

        File.WriteAllText(helper, "#!/bin/sh\n" + dispatch + body + "\n")

        File.SetUnixFileMode(
            helper,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )

        use owned = new ProcessSupervision.Session(helper, command)

        refused owned.Target message

        Expect.isFalse (owned.Stop()) "Unconfirmed ownership never reports successful cleanup"

let private execFailure () =
    let completedExit = BoundedProcess.run (start "exit 127") None 64 2000
    Expect.equal completedExit.ExitCode 127 "A genuine target exit 127 remains a normal result"
    let command = start "exit 0"
    command.FileName <- "/claimcore-synthetic-absent-executable"
    use owned = new ProcessSupervision.Session(ProcessSupervision.executable, command)
    refused owned.Target "Test process target could not start."
    Expect.isTrue (owned.Stop()) "Exec failure still reaches anchored teardown"

let private startupEvidence () =
    for reported, stopped, accepted in
        [
            true, true, false
            true, false, false
            false, true, true
            false, false, false
        ] do
        let error = ProcessStartupFailure(reported)

        Expect.equal
            (BoundedProcess.cleanupSucceeded error stopped)
            accepted
            "Startup uncertainty survives an unassigned session"

    use expired = new Threading.CancellationTokenSource()
    expired.Cancel()

    Expect.throwsC
        (fun () ->
            ProcessResolution.resolve ProcessSupervision.executable "/bin/sh" "" expired.Token
            |> ignore)
        (fun error ->
            let failure = error :?> ProcessStartupFailure

            Expect.isFalse
                failure.CleanupFailed
                "Already-expired resolution starts no helper or target")

let private resolutionFixture () =
    let command =
        ProcessStartInfo(
            "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    CliProcessEnvironment.clearInherited command

    [
        "fsi"
        "--nologo"
        "--quiet"
        "--exec"
        Path.Combine(AppContext.BaseDirectory, "ProcessResolutionFixture.fsx")
    ]
    |> List.iter command.ArgumentList.Add

    let result = BoundedProcess.run command None 65536 15000

    Expect.equal
        result.ExitCode
        0
        "Isolated real Process.Start resolver differential fixture completes"

    Expect.equal
        ((Encoding.UTF8.GetString result.StandardOutput).Trim())
        "RESOLUTION_PARITY_OK:15"
        "Every requested differential case executes"

    Expect.isEmpty result.StandardError "Resolver fixture emits only its closed result"

let private exactInputs directory =
    File.WriteAllText(Path.Combine(directory, "cwd-marker"), "synthetic")

    let command =
        start
            "test -f cwd-marker || exit 3; printf '%s\\n' \"${CLAIMCORE_SUPERVISION_ABSENT-unset}\" \"$1\"; cat"

    command.WorkingDirectory <- directory
    command.ArgumentList.Add "name"
    command.ArgumentList.Add "one argument ' with spaces"
    let previous = Environment.GetEnvironmentVariable "CLAIMCORE_SUPERVISION_ABSENT"

    try
        Environment.SetEnvironmentVariable("CLAIMCORE_SUPERVISION_ABSENT", "synthetic-inherited")
        command.Environment.Remove("CLAIMCORE_SUPERVISION_ABSENT") |> ignore
        command.RedirectStandardInput <- true
        let bytes = Encoding.UTF8.GetBytes "exact-λ-input"
        let result = BoundedProcess.run command (Some bytes) 4096 2000
        Expect.equal result.ExitCode 0 "Target runs in its specified working directory"

        Expect.equal
            (Encoding.UTF8.GetString result.StandardOutput)
            "unset\none argument ' with spaces\nexact-λ-input"
            "Environment removals, arguments and stdin bytes survive supervision"

        Expect.isEmpty result.StandardError "Independent stderr remains empty"
    finally
        Environment.SetEnvironmentVariable("CLAIMCORE_SUPERVISION_ABSENT", previous)

[<Tests>]
let tests =
    testCase
        "test process supervision retains group ownership across target exit and controller EOF"
        (fun () ->
            let directory =
                Directory.CreateTempSubdirectory("claimcore-process-supervision-").FullName

            try
                protocolControls ()
                earlyTeardown ()
                exitedLeader ()
                completedLauncher ()
                controllerEof ()
                statusPipeFailure ()
                signalRestoration ()
                refusalControls directory
                execFailure ()
                exactInputs directory
                startupEvidence ()
                resolutionFixture ()
            finally
                Directory.Delete(directory, true))
