namespace ClaimCore.TestSupport

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks

[<NoEquality; NoComparison>]
type BoundedProcessResult =
    {
        ExitCode: int
        StandardOutput: byte array
        StandardError: byte array
    }

type ProcessFailureKind =
    | Deadline
    | Output
    | Delivery

type BoundedProcessFailure(kind, stdout: byte array, stderr: byte array, cleanupFailed: bool) =
    inherit Exception($"Test process failed: {kind}; cleanupFailed={cleanupFailed}.")
    member _.Kind: ProcessFailureKind = kind
    member _.StandardOutput = stdout
    member _.StandardError = stderr
    member _.CleanupFailed = cleanupFailed

module BoundedProcess =
    type private Capture(maximum) =
        let bytes = Array.zeroCreate<byte>(maximum + 1)
        let mutable count = 0
        member _.Bytes = bytes
        member _.Count = Volatile.Read(&count)

        member _.Advance received =
            Volatile.Write(&count, count + received)

        member this.Snapshot() =
            Array.take (min maximum this.Count) bytes

    let private readInto (capture: Capture) (stream: Stream) (deadline: CancellationTokenSource) =
        task {
            try
                let mutable reading = true

                while reading && capture.Count < capture.Bytes.Length do
                    let! received =
                        stream.ReadAsync(capture.Bytes.AsMemory(capture.Count), deadline.Token)

                    capture.Advance received
                    reading <- received <> 0

                if capture.Count = capture.Bytes.Length then
                    invalidOp "Test process output exceeded its allowance."

                return capture.Snapshot()
            with error ->
                deadline.Cancel()
                return raise error
        }

    let read maximum stream deadline =
        readInto (Capture maximum) stream deadline

    /// Interactive fixtures retain their stdin/process ownership but bound each actual pipe reader.
    let readText maximum (reader: StreamReader) timeout =
        task {
            use deadline = new CancellationTokenSource(timeout: TimeSpan)
            let! bytes = read maximum reader.BaseStream deadline
            return System.Text.Encoding.UTF8.GetString(bytes)
        }

    let private writeInput (child: Process) input (deadline: CancellationTokenSource) =
        task {
            match input with
            | Some(bytes: byte array) ->
                do! child.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(), deadline.Token)
                child.StandardInput.Close()
            | None -> ()
        }

    let private failureKind
        maximum
        timeout
        (elapsed: Stopwatch)
        (stdout: Capture)
        (stderr: Capture)
        =
        if stdout.Count > maximum || stderr.Count > maximum then
            Output
        elif elapsed.ElapsedMilliseconds >= int64 timeout then
            Deadline
        else
            Delivery

    let captureFailure kind stdout stderr cleanup =
        let cleanupFailed =
            try
                not (cleanup ())
            with _ ->
                true

        BoundedProcessFailure(kind, stdout (), stderr (), cleanupFailed)

    let private stopChild (child: Process) started =
        if not started || child.HasExited then
            true
        else
            child.Kill(true)
            child.WaitForExit(2000)

    let run (start: ProcessStartInfo) input maximum timeoutMilliseconds =
        if maximum < 1 || maximum >= Int32.MaxValue || timeoutMilliseconds < 1 then
            invalidArg (nameof maximum) "Test process bounds must be positive."

        use child = new Process(StartInfo = start)
        let elapsed = Stopwatch.StartNew()
        use deadline = new CancellationTokenSource(timeoutMilliseconds)
        let stdout = Capture maximum
        let stderr = Capture maximum
        let mutable started = false

        try
            started <- child.Start()

            if not started then
                invalidOp "Test process did not start."

            let output = readInto stdout child.StandardOutput.BaseStream deadline
            let errors = readInto stderr child.StandardError.BaseStream deadline
            let writing = writeInput child input deadline
            let exited = child.WaitForExitAsync(deadline.Token)

            let completed =
                Task.WhenAll([| output :> Task; errors :> Task; writing :> Task; exited |])

            let winner =
                Task.WhenAny(completed, Task.Delay(timeoutMilliseconds)).GetAwaiter().GetResult()

            if not (obj.ReferenceEquals(winner, completed)) then
                invalidOp "Test process exceeded its deadline."

            completed.GetAwaiter().GetResult()

            {
                ExitCode = child.ExitCode
                StandardOutput = output.Result
                StandardError = errors.Result
            }
        with _ ->
            let kind = failureKind maximum timeoutMilliseconds elapsed stdout stderr
            deadline.Cancel()

            raise (
                captureFailure kind stdout.Snapshot stderr.Snapshot (fun () ->
                    stopChild child started)
            )
