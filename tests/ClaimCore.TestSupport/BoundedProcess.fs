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

    let private failureKind maximum deadlineExpired (stdout: Capture) (stderr: Capture) =
        if stdout.Count > maximum || stderr.Count > maximum then
            Output
        elif deadlineExpired then
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

    let private finishReaders milliseconds (readers: Task array) =
        try
            Task.WhenAll(readers).Wait(milliseconds: int) |> ignore
        with _ ->
            ()

        readers |> Array.forall _.IsCompleted

    let internal stop (child: Process) started (readers: Task array) =
        let timer = Stopwatch.StartNew()

        let remaining () =
            max 1 (2000 - int timer.ElapsedMilliseconds)

        try
            if started && not child.HasExited then
                child.Kill(entireProcessTree = true)

            let exited = not started || child.WaitForExit(remaining ())
            let finished = finishReaders (remaining ()) readers
            exited && finished && timer.ElapsedMilliseconds < 2000L
        with _ ->
            false

    let private execute (child: Process) input stdout stderr (deadline: CancellationTokenSource) =
        let output = readInto stdout child.StandardOutput.BaseStream deadline
        let errors = readInto stderr child.StandardError.BaseStream deadline
        let writing = writeInput child input deadline
        let exited = child.WaitForExitAsync(deadline.Token)
        let readers = [| output :> Task; errors :> Task; writing :> Task |]
        readers, Task.WhenAll(Array.append readers [| exited |]), output, errors

    let run (start: ProcessStartInfo) input maximum timeoutMilliseconds =
        if maximum < 1 || maximum >= Int32.MaxValue || timeoutMilliseconds < 1 then
            invalidArg (nameof maximum) "Test process bounds must be positive."

        use expiry = new CancellationTokenSource(timeoutMilliseconds)
        use deadline = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token)
        use child = new Process(StartInfo = start)
        let stdout = Capture maximum
        let stderr = Capture maximum
        let mutable readers = [||]
        let mutable started = false

        try
            started <- child.Start()

            if not started then
                invalidOp "Test process could not start."

            let active, completed, output, errors = execute child input stdout stderr deadline
            readers <- active
            completed.WaitAsync(expiry.Token).GetAwaiter().GetResult()

            if not (stop child started readers) then
                invalidOp "Test process cleanup was incomplete."

            {
                ExitCode = child.ExitCode
                StandardOutput = output.Result
                StandardError = errors.Result
            }
        with _ ->
            let kind = failureKind maximum expiry.IsCancellationRequested stdout stderr
            deadline.Cancel()

            raise (
                captureFailure kind stdout.Snapshot stderr.Snapshot (fun () ->
                    stop child started readers)
            )

[<NoEquality; NoComparison>]
type ServiceLogSnapshot =
    {
        Bytes: byte array
        TotalBytes: int64
        Complete: bool
    }

    member this.Truncated = this.TotalBytes > int64 this.Bytes.Length

/// Service logs can grow throughout a fixture. Retain bytes, but never stop draining a full pipe.
type ServiceLogCapture(maximum: int, stream: Stream, cancellation: CancellationToken) =
    do
        if maximum < 1 then
            invalidArg (nameof maximum) "A service log allowance must be positive."

    let gate = obj ()
    let window = Array.zeroCreate<byte> maximum
    let mutable cursor = 0
    let mutable retained = 0
    let mutable total = 0L

    let append (bytes: byte array) count =
        lock gate (fun () ->
            if count >= maximum then
                Buffer.BlockCopy(bytes, count - maximum, window, 0, maximum)
                cursor <- 0
            else
                let first = min count (maximum - cursor)
                Buffer.BlockCopy(bytes, 0, window, cursor, first)
                Buffer.BlockCopy(bytes, first, window, 0, count - first)
                cursor <- (cursor + count) % maximum

            retained <- min maximum (retained + count)
            total <- total + int64 count)

    let completion =
        task {
            let chunk = Array.zeroCreate<byte> 8192
            let mutable reading = true

            while reading do
                let! count = stream.ReadAsync(chunk.AsMemory(), cancellation)
                append chunk count
                reading <- count <> 0
        }

    member _.Completion = completion :> Task

    member _.Snapshot() =
        lock gate (fun () ->
            let bytes = Array.zeroCreate<byte> retained
            let start = if retained = maximum then cursor else 0
            let first = min retained (maximum - start)
            Buffer.BlockCopy(window, start, bytes, 0, first)
            Buffer.BlockCopy(window, 0, bytes, first, retained - first)

            {
                Bytes = bytes
                TotalBytes = total
                Complete = completion.IsCompletedSuccessfully
            })

[<NoEquality; NoComparison>]
type ServiceProcessSnapshot =
    {
        StandardOutput: ServiceLogSnapshot
        StandardError: ServiceLogSnapshot
        CleanupSucceeded: bool
    }

/// The fixture owns service lifetime; command deadlines and fail-on-output limits stay separate.
type OwnedServiceProcess(start: ProcessStartInfo, stdoutMaximum: int, stderrMaximum: int) =
    do
        if
            stdoutMaximum < 1
            || stderrMaximum < 1
            || not start.RedirectStandardOutput
            || not start.RedirectStandardError
        then
            invalidArg
                (nameof start)
                "Owned service logs require redirected pipes and positive allowances."

    let lifetime = new CancellationTokenSource()

    let child =
        try
            Process.Start start
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "The owned service did not start.")
        with _ ->
            lifetime.Dispose()
            reraise ()

    let initialized = ResizeArray<Task>()

    let capture maximum stream =
        try
            let log = ServiceLogCapture(maximum, stream, lifetime.Token)
            initialized.Add log.Completion
            log
        with _ ->
            BoundedProcess.stop child true (initialized.ToArray()) |> ignore
            lifetime.Cancel()
            lifetime.Dispose()
            child.Dispose()
            reraise ()

    let output = capture stdoutMaximum child.StandardOutput.BaseStream
    let errors = capture stderrMaximum child.StandardError.BaseStream
    let readers = initialized.ToArray()
    let gate = obj ()
    let mutable stopping = 0
    let mutable stopped: ServiceProcessSnapshot option = None
    let mutable disposed = false

    do
        for reader in readers do
            reader.ContinueWith(
                Action<Task>(fun failed ->
                    if failed.IsFaulted && Volatile.Read(&stopping) = 0 then
                        try
                            if not child.HasExited then
                                child.Kill(true)
                        with _ ->
                            ()),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            )
            |> ignore

    member _.Id = child.Id
    member _.HasExited = child.HasExited
    member _.ExitCode = child.ExitCode
    member _.WaitForExitAsync(token: CancellationToken) = child.WaitForExitAsync token
    member _.StandardInput = child.StandardInput

    member _.Output = output.Snapshot()
    member _.Errors = errors.Snapshot()

    member _.Stop() =
        lock gate (fun () ->
            match stopped with
            | Some snapshot -> snapshot
            | None ->
                Interlocked.Exchange(&stopping, 1) |> ignore
                let cleaned = BoundedProcess.stop child true readers

                if not cleaned then
                    lifetime.Cancel()

                    try
                        Task.WhenAll(readers).Wait(500) |> ignore
                    with _ ->
                        ()

                let snapshot =
                    {
                        StandardOutput = output.Snapshot()
                        StandardError = errors.Snapshot()
                        CleanupSucceeded = cleaned
                    }

                stopped <- Some snapshot
                snapshot)

    interface IDisposable with
        member this.Dispose() =
            this.Stop() |> ignore

            lock gate (fun () ->
                if not disposed then
                    lifetime.Cancel()
                    lifetime.Dispose()
                    child.Dispose()
                    disposed <- true)
