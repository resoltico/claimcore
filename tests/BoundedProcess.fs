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

    let cleanupSucceeded (error: exn) stopped =
        match error with
        | :? ProcessStartupFailure as failed -> stopped && not failed.CleanupFailed
        | _ -> stopped

    let private awaitTarget
        (owned: ProcessSupervision.Session)
        (deadline: CancellationTokenSource)
        =
        task {
            try
                return! owned.Target.WaitAsync(deadline.Token)
            with error ->
                deadline.Cancel()
                return raise error
        }

    let private finishReaders milliseconds (readers: Task array) =
        try
            Task.WhenAll(readers).Wait(milliseconds: int) |> ignore
        with _ ->
            ()

        readers |> Array.forall _.IsCompleted

    let run (start: ProcessStartInfo) input maximum timeoutMilliseconds =
        if maximum < 1 || maximum >= Int32.MaxValue || timeoutMilliseconds < 1 then
            invalidArg (nameof maximum) "Test process bounds must be positive."

        use expiry = new CancellationTokenSource(timeoutMilliseconds)
        use deadline = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token)
        let stdout = Capture maximum
        let stderr = Capture maximum
        let mutable session: ProcessSupervision.Session option = None
        let mutable readers = [||]
        let mutable completedTarget = false

        use release =
            { new IDisposable with
                member _.Dispose() =
                    session |> Option.iter (fun owned -> (owned :> IDisposable).Dispose())
            }

        let stop () =
            let timer = Stopwatch.StartNew()
            let stopped = session |> Option.forall _.Stop()

            finishReaders (max 1 (2000 - int timer.ElapsedMilliseconds)) readers
            && stopped
            && timer.ElapsedMilliseconds < 2000L

        try
            let owned =
                new ProcessSupervision.Session(
                    ProcessSupervision.executable,
                    start,
                    cancellationToken = expiry.Token
                )

            session <- Some owned
            let child = owned.Process
            let output = readInto stdout child.StandardOutput.BaseStream deadline
            let errors = readInto stderr child.StandardError.BaseStream deadline
            let writing = writeInput child input deadline
            let exited = awaitTarget owned deadline
            readers <- [| output :> Task; errors :> Task; writing :> Task |]
            let completed = Task.WhenAll(Array.append readers [| exited :> Task |])
            completed.WaitAsync(expiry.Token).GetAwaiter().GetResult()
            completedTarget <- true

            if not (stop ()) then
                invalidOp "Test process cleanup was incomplete."

            {
                ExitCode = exited.Result
                StandardOutput = output.Result
                StandardError = errors.Result
            }
        with error ->
            let kind =
                if completedTarget then
                    Delivery
                else
                    failureKind maximum expiry.IsCancellationRequested stdout stderr

            deadline.Cancel()

            let cleanup () = cleanupSucceeded error (stop ())

            raise (captureFailure kind stdout.Snapshot stderr.Snapshot cleanup)
