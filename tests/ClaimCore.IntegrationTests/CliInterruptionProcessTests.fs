module ClaimCore.IntegrationTests.CliInterruptionProcessTests

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Threading
open Expecto
open ClaimCore.TestSupport

let private startCli () =
    let root = RepositoryRoot.find ()

    let binary =
        Path.Combine(root, "artifacts/bin/ClaimCore.Cli/release/ClaimCore.Cli.dll")

    let start = ProcessStartInfo("dotnet")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardInput <- true
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.ArgumentList.Add(binary)
    start.ArgumentList.Add("session")

    CliProcessEnvironment.clearInherited start

    let child =
        Process.Start(start)
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic CLI failed to start")

    child

let private beforeInputEof () =
    use child = startCli ()
    use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 5.)
    use errorStream = child.StandardError
    use output = child.StandardOutput
    let errors = errorStream.ReadToEndAsync(deadline.Token)

    try
        child.StandardInput.WriteLine("{invalid}")
        child.StandardInput.Flush()
        let first = output.ReadLineAsync(deadline.Token).GetAwaiter().GetResult()

        Expect.isTrue
            (not (String.IsNullOrEmpty first))
            "Completed first frame proves signal handler is installed"

        let signal = ProcessStartInfo("/bin/kill")
        signal.UseShellExecute <- false
        signal.ArgumentList.Add("-INT")
        signal.ArgumentList.Add(child.Id.ToString(CultureInfo.InvariantCulture))

        use sent =
            Process.Start(signal)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failtest "Signal sender failed to start")

        Expect.isTrue (sent.WaitForExit(2000)) "Signal sender completed"
        Expect.equal sent.ExitCode 0 "Signal targets only the owned child"
        child.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult()

        Expect.equal
            child.ExitCode
            130
            "Pre-dispatch interruption exits without waiting for stdin EOF"

        Expect.equal
            (errors.GetAwaiter().GetResult()).Length
            0
            "No invented failure or payload on stderr"
    finally
        if not child.HasExited then
            child.Kill(true)

        child.WaitForExit()

        try
            errors.GetAwaiter().GetResult() |> ignore
        with _ ->
            ()

let private afterMutationDispatch () =
    let root = RepositoryRoot.find ()
    let start = ProcessStartInfo("node")
    start.WorkingDirectory <- root
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.ArgumentList.Add("eng/ci/fixtures/cli-interruption.mjs")

    start.ArgumentList.Add(
        Path.Combine(root, "artifacts/bin/ClaimCore.Cli/release/ClaimCore.Cli.dll")
    )

    let result = BoundedProcess.run start None 65536 20000
    Expect.equal result.ExitCode 0 "Real TLS dispatch followed by SIGINT remains uncertain"

let tests =
    testList
        "CLI process interruption"
        [
            testCase
                "[CC-CLI-003] SIGINT after real TLS mutation dispatch exits uncertain"
                afterMutationDispatch
            testCase
                "[CC-CLI-003] SIGINT ends input wait before EOF and mutation admission"
                beforeInputEof
        ]
