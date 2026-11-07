module ClaimCore.AcceptanceTests.ProcessFailureTests

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.TestSupport

let private start script input =
    let child =
        ProcessStartInfo(
            "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input
        )

    child.ArgumentList.Add "-c"
    child.ArgumentList.Add script
    child

let private failure kind script (input: byte array option) maximum timeout =
    try
        BoundedProcess.run (start script input.IsSome) input maximum timeout |> ignore
        failtest "The synthetic child must exceed its configured process contract."
    with :? BoundedProcessFailure as failed ->
        Expect.equal failed.Kind kind "Safe failure category"
        Expect.isLessThanOrEqual failed.StandardOutput.Length maximum "Bounded partial stdout"
        Expect.isLessThanOrEqual failed.StandardError.Length maximum "Bounded partial stderr"

        Expect.isFalse
            (failed.Message.Contains("synthetic"))
            "Exception text contains no child output"

        failed

let private outputControls () =
    let output = failure Output "printf synthetic-overflow" None 4 2000

    Expect.equal
        (Encoding.UTF8.GetString output.StandardOutput)
        "synt"
        "Overflow retains only allowance"

    let errors = failure Output "printf synthetic-overflow >&2" None 4 2000

    Expect.equal
        (Encoding.UTF8.GetString errors.StandardError)
        "synt"
        "Stderr has the same allowance"

let private cleanupControls () =
    let bytes () =
        Encoding.UTF8.GetBytes "synthetic-private-output"

    for cleanup in [ (fun () -> false); (fun () -> invalidOp "synthetic-cleanup-detail") ] do
        let evidence = BoundedProcess.captureFailure Deadline bytes bytes cleanup
        Expect.equal evidence.Kind Deadline "Cleanup cannot overwrite the main failure"
        Expect.isTrue evidence.CleanupFailed "Cleanup race or incomplete cleanup is explicit"
        Expect.equal evidence.StandardOutput (bytes ()) "Cleanup cannot discard private evidence"
        Expect.equal evidence.StandardError (bytes ()) "Stderr remains available"

        Expect.isFalse
            (evidence.Message.Contains("synthetic"))
            "Cleanup details stay out of failure text"

    let completed = BoundedProcess.captureFailure Delivery bytes bytes (fun () -> true)
    Expect.isFalse completed.CleanupFailed "Completed cleanup has no failure flag"

let private descendantCleanup () =
    let result =
        failure
            Deadline
            "sleep 30 & descendant=$!; printf '%s\n' \"$descendant\"; wait \"$descendant\""
            None
            64
            500

    let pid = Int32.Parse(Encoding.ASCII.GetString(result.StandardOutput).Trim())

    let child =
        try
            Some(Process.GetProcessById pid)
        with :? ArgumentException ->
            None

    child
    |> Option.iter (fun descendant ->
        use owned = descendant

        try
            Expect.isTrue
                (descendant.HasExited || descendant.WaitForExit 1000)
                "The owned waiting descendant is terminated with its parent"
        finally
            if not descendant.HasExited then
                descendant.Kill()
                descendant.WaitForExit 1000 |> ignore)

    Expect.isFalse result.CleanupFailed "Owned process-tree cleanup is observed"

let private controls () =
    cleanupControls ()

    let deadline = failure Deadline "exec sleep 2" None 64 250

    Expect.isEmpty deadline.StandardOutput "The quiet deadline child writes no stdout"
    Expect.isEmpty deadline.StandardError "The quiet deadline child writes no stderr"
    Expect.isFalse deadline.CleanupFailed "The owned quiet child completed cleanup"

    outputControls ()
    descendantCleanup ()

    let delivery =
        failure
            Delivery
            "printf synthetic-out; printf synthetic-err >&2; exec 0<&-; sleep 0.1"
            (Some(Array.zeroCreate<byte> 1_048_576))
            64
            2000

    Expect.equal (Encoding.UTF8.GetString delivery.StandardOutput) "synthetic-out" "Delivery stdout"
    Expect.equal (Encoding.UTF8.GetString delivery.StandardError) "synthetic-err" "Delivery stderr"

    let completed =
        BoundedProcess.run
            (start "printf synthetic-out; printf synthetic-err >&2; exit 7" false)
            None
            64
            2000

    Expect.equal completed.ExitCode 7 "A completed nonzero child remains a normal result"

    Expect.equal
        (Encoding.UTF8.GetString completed.StandardOutput)
        "synthetic-out"
        "Completed stdout"

    Expect.equal
        (Encoding.UTF8.GetString completed.StandardError)
        "synthetic-err"
        "Completed stderr"

[<Tests>]
let tests =
    testCase
        "bounded child failures retain partial private evidence without exposing output"
        controls

[<Tests>]
let publicationTests =
    testCase
        "failed publication retains bounded private output before fixture startup"
        PublishedProcesses.publicationFailure

type private FragmentedLog(bytes: byte array, chunk: int) =
    inherit MemoryStream(bytes)

    override _.ReadAsync(buffer: Memory<byte>, token) =
        base.ReadAsync(buffer.Slice(0, min chunk buffer.Length), token)

type private OpenLog() =
    inherit MemoryStream()

    override _.ReadAsync(_: Memory<byte>, token) =
        ValueTask<int>(
            task {
                do! Task.Delay(Timeout.Infinite, token)
                return 0
            }
        )

type private FailedLog() =
    inherit MemoryStream()

    override _.ReadAsync(_: Memory<byte>, _: CancellationToken) =
        ValueTask<int>(Task.FromException<int>(IOException "Owned synthetic log read failed."))

let private serviceLogWindows () =
    let unicode = Encoding.UTF8.GetBytes "start 😀 العربية end"

    for bytes in [ [||]; Array.init 37 byte; unicode ] do
        for maximum in [ 1; 4; 8; 16; 64 ] do
            for fragment in [ 1; 3; 32 ] do
                use stream = new FragmentedLog(bytes, fragment)
                let capture = ServiceLogCapture(maximum, stream, CancellationToken.None)
                capture.Completion.GetAwaiter().GetResult()
                let actual = capture.Snapshot()
                let expected = bytes |> Array.skip (max 0 (bytes.Length - maximum))

                Expect.equal
                    actual.Bytes
                    expected
                    "The exact final bytes survive wrap and fragmentation"

                Expect.equal actual.TotalBytes (int64 bytes.Length) "Every drained byte is counted"
                Expect.equal actual.Truncated (bytes.Length > maximum) "Tail truncation is explicit"
                Expect.isTrue actual.Complete "Only EOF completes a log capture"

    use cancellation = new CancellationTokenSource()
    use openLog = new OpenLog()
    let pending = ServiceLogCapture(8, openLog, cancellation.Token)
    Expect.isFalse pending.Completion.IsCompleted "An open service pipe waits for its owner"
    cancellation.Cancel()

    try
        pending.Completion.WaitAsync(TimeSpan.FromSeconds 1.).GetAwaiter().GetResult()
        failtest "Owner cancellation must end an unclosed pipe read"
    with :? OperationCanceledException ->
        ()

    Expect.isTrue
        pending.Completion.IsCanceled
        "The owner, rather than a wait timeout, canceled the reader"

    Expect.isFalse (pending.Snapshot().Complete) "Cancellation cannot masquerade as complete logs"
    use failedLog = new FailedLog()
    let failed = ServiceLogCapture(8, failedLog, CancellationToken.None)

    Expect.throwsT<IOException>
        (fun () -> failed.Completion.GetAwaiter().GetResult())
        "An unexpected reader failure remains observable"

    Expect.isFalse (failed.Snapshot().Complete) "An I/O failure cannot masquerade as EOF"

let private failStopLogControl () =
    use child =
        Process.Start(start "head -c 8388608 /dev/zero >&2; printf DONE" false)
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "The controlled log producer must start")

    use lifetime = new CancellationTokenSource()
    let output = BoundedProcess.read 64 child.StandardOutput.BaseStream lifetime
    let errors = BoundedProcess.read 1_048_576 child.StandardError.BaseStream lifetime

    let cleanup () =
        if not child.HasExited then
            child.Kill(true)

        Expect.isTrue (child.WaitForExit(2000)) "The controlled blocked producer terminates"

    try
        Expect.throwsT<InvalidOperationException>
            (fun () -> errors.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult() |> ignore)
            "The command-style reader stops at the former service stderr allowance"

        Expect.isTrue
            lifetime.IsCancellationRequested
            "A full command capture cancels its peer reader"

        Expect.isFalse
            child.HasExited
            "Stopping the service pipe reader blocks later producer progress"

        try
            output.WaitAsync(TimeSpan.FromSeconds 1.).GetAwaiter().GetResult() |> ignore
            failtest "The peer reader must be canceled"
        with :? OperationCanceledException ->
            ()
    with error ->
        FixtureDisposal.afterFailure error [ cleanup ]
        reraise ()

    cleanup ()

let private serviceLogProcess () =
    failStopLogControl ()

    use child =
        new OwnedServiceProcess(
            start
                "head -c 131072 /dev/zero; head -c 131072 /dev/zero >&2; IFS= read -r gate; printf DONE-OUT; printf DONE-ERR >&2"
                true,
            64,
            48
        )

    Expect.isTrue
        (SpinWait.SpinUntil(
            (fun () -> child.Output.TotalBytes = 131072L && child.Errors.TotalBytes = 131072L),
            3000
        ))
        "Both pipes keep draining far beyond their retained windows"

    Expect.isFalse
        child.HasExited
        "The service remains available after exceeding log capture limits"

    child.StandardInput.WriteLine "continue"
    child.StandardInput.Flush()
    use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 3.)
    child.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult()
    let captured = child.Stop()
    Expect.isTrue captured.CleanupSucceeded "The owned service exits and its readers join"

    for actual, maximum, marker in
        [
            captured.StandardOutput, 64, "DONE-OUT"
            captured.StandardError, 48, "DONE-ERR"
        ] do
        let expected =
            Array.append
                (Array.zeroCreate<byte>(maximum - marker.Length))
                (Encoding.ASCII.GetBytes marker)

        Expect.equal actual.Bytes expected "Later service activity remains in the bounded tail"
        Expect.equal actual.TotalBytes 131080L "Overflow and later output are both counted"
        Expect.isTrue actual.Truncated "Large logs are explicitly marked truncated"
        Expect.isTrue actual.Complete "Both service streams reach EOF"

    use waiting = new OwnedServiceProcess(start "sleep 30 & wait" false, 64, 64)
    let elapsed = Stopwatch.StartNew()
    let stopped = waiting.Stop()
    Expect.isTrue stopped.CleanupSucceeded "Fixture teardown kills the owned waiting process tree"

    Expect.isLessThan
        elapsed.Elapsed
        (TimeSpan.FromSeconds 3.)
        "Service teardown has a fixed cleanup bound"

    Expect.isTrue stopped.StandardOutput.Complete "Killed service stdout drains to EOF"
    Expect.isTrue stopped.StandardError.Complete "Killed service stderr drains to EOF"

let private serviceLogOpenPipe () =
    use child =
        new OwnedServiceProcess(
            start "sleep 30 & descendant=$!; printf '%s\\n' \"$descendant\"; exit 0" false,
            64,
            64
        )

    Expect.isTrue
        (SpinWait.SpinUntil((fun () -> child.HasExited && child.Output.TotalBytes > 0L), 3000))
        "The owned parent exits while its synthetic descendant holds both pipes"

    let pid = Int32.Parse(Encoding.ASCII.GetString(child.Output.Bytes).Trim())

    let cleanup () =
        try
            use descendant = Process.GetProcessById pid

            if not descendant.HasExited then
                descendant.Kill(true)

                Expect.isTrue
                    (descendant.WaitForExit(1000))
                    "The synthetic inherited-pipe holder terminates"
        with :? ArgumentException ->
            ()

    try
        let elapsed = Stopwatch.StartNew()
        let captured = child.Stop()
        (child :> IDisposable).Dispose()

        Expect.isLessThan
            elapsed.Elapsed
            (TimeSpan.FromSeconds 4.)
            "An unclosed inherited pipe cannot make fixture disposal wait indefinitely"

        Expect.isFalse captured.CleanupSucceeded "Incomplete pipe cleanup remains explicit"
        Expect.isFalse captured.StandardOutput.Complete "Canceled stdout cannot claim EOF"
        Expect.isFalse captured.StandardError.Complete "Canceled stderr cannot claim EOF"
    with error ->
        FixtureDisposal.afterFailure error [ cleanup ]
        reraise ()

    cleanup ()

let private serviceLogMetadata () =
    let captured: ServiceProcessSnapshot =
        {
            StandardOutput =
                {
                    Bytes = Encoding.UTF8.GetBytes "private-marker"
                    TotalBytes = 100L
                    Complete = false
                }
            StandardError =
                {
                    Bytes = [||]
                    TotalBytes = 0L
                    Complete = true
                }
            CleanupSucceeded = false
        }

    let bytes = PublishedProcesses.serviceCaptureMetadata captured

    Expect.isFalse
        ((Encoding.UTF8.GetString bytes).Contains "private-marker")
        "Metadata excludes log contents"

    use document = JsonDocument.Parse bytes
    let root = document.RootElement

    Expect.equal
        (root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
        (set [ "stdout"; "stderr"; "cleanupSucceeded" ])
        "The report has only the owned stream projections and cleanup flag"

    for name in [ "stdout"; "stderr" ] do
        Expect.equal
            (root.GetProperty(name).EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
            (set [ "totalBytes"; "retainedBytes"; "truncated"; "complete" ])
            "Stream metadata contains only byte counts and completion flags"

    let stdout = root.GetProperty "stdout"
    Expect.equal (stdout.GetProperty("totalBytes").GetInt64()) 100L "Total source bytes"
    Expect.equal (stdout.GetProperty("retainedBytes").GetInt32()) 14 "Retained byte count"
    Expect.isTrue (stdout.GetProperty("truncated").GetBoolean()) "Lost history is explicit"
    Expect.isFalse (stdout.GetProperty("complete").GetBoolean()) "Incomplete capture is explicit"

    Expect.isFalse
        (root.GetProperty("cleanupSucceeded").GetBoolean())
        "Incomplete cleanup is explicit"

    let invalid =
        new ProcessStartInfo(RedirectStandardOutput = true, RedirectStandardError = true)

    Expect.throwsT<ArgumentException>
        (fun () ->
            use _ = new OwnedServiceProcess(invalid, 0, 8)
            ())
        "Invalid capture allowances are refused before a process can start"

[<Tests>]
let serviceLogTests =
    testList
        "owned service logs"
        [
            testCase
                "retain exact bounded tails and distinguish EOF, cancellation and failure"
                serviceLogWindows
            testCase
                "continue draining after capture limits and stop with their fixture"
                serviceLogProcess
            testCase "bound teardown when an inherited pipe remains open" serviceLogOpenPipe
            testCase "publish only byte counts and completion flags" serviceLogMetadata
        ]
