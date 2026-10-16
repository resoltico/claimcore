namespace ClaimCore.TestSupport

open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Threading
open System.Threading.Tasks

/// Test-only lifetime ownership; no product assembly references this module or its native helper.
module ProcessSupervision =
    let executable = Path.Combine(AppContext.BaseDirectory, "claimcore-test-supervisor")

    let private validate (start: ProcessStartInfo) =
        if
            start.UseShellExecute
            || not start.RedirectStandardOutput
            || not start.RedirectStandardError
            || not (String.IsNullOrEmpty start.Arguments)
            || not (String.IsNullOrEmpty start.UserName)
            || not (String.IsNullOrEmpty start.Verb)
            || start.ErrorDialog
            || start.ErrorDialogParentHandle <> IntPtr.Zero
            || start.WindowStyle <> ProcessWindowStyle.Normal
        then
            invalidArg (nameof start) "Unsupported supervised test process options."

        if not (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) then
            invalidOp "Test process supervision requires Linux or macOS."

    let private configuration helper (start: ProcessStartInfo) control status cancellation =
        validate start

        let value =
            ProcessStartInfo(
                helper,
                UseShellExecute = false,
                RedirectStandardInput = start.RedirectStandardInput,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = start.WorkingDirectory,
                CreateNoWindow = start.CreateNoWindow
            )

        value.StandardInputEncoding <- start.StandardInputEncoding
        value.StandardOutputEncoding <- start.StandardOutputEncoding
        value.StandardErrorEncoding <- start.StandardErrorEncoding
        value.Environment.Clear()

        for pair in start.Environment do
            value.Environment[pair.Key] <- pair.Value

        let executableDirectory =
            Environment.ProcessPath
            |> Option.ofObj
            |> Option.map Path.GetDirectoryName
            |> Option.bind Option.ofObj
            |> Option.defaultValue ""

        let resolved =
            ProcessResolution.resolve helper start.FileName executableDirectory cancellation

        cancellation.ThrowIfCancellationRequested()

        [ control; status; "--"; resolved; start.FileName ]
        |> List.iter value.ArgumentList.Add

        start.ArgumentList |> Seq.iter value.ArgumentList.Add
        value

    let parseTargetStatus line =
        match line with
        | "F" -> invalidOp "Test process target could not start."
        | value when value.StartsWith("E ", StringComparison.Ordinal) ->
            match Int32.TryParse(value.AsSpan(2), Globalization.NumberStyles.None, null) with
            | true, code when code >= 0 && code <= 255 -> code
            | _ -> invalidOp "Invalid test process status."
        | value when value.StartsWith("K ", StringComparison.Ordinal) ->
            match Int32.TryParse(value.AsSpan(2), Globalization.NumberStyles.None, null) with
            | true, signal when signal >= 1 && signal <= 127 -> 128 + signal
            | _ -> invalidOp "Invalid test process status."
        | _ -> invalidOp "Invalid test process status."

    let private advance (target: TaskCompletionSource<int>) stage line =
        match stage, line with
        | 0, "S" -> 1
        | 0, "T" -> 3
        | 0, "F" ->
            target.TrySetException(
                InvalidOperationException("Test process target could not start.")
            )
            |> ignore

            2
        | 1, "T" -> 3
        | 1, value ->
            target.SetResult(parseTargetStatus value)
            2
        | 2, "T" -> 3
        | _ -> invalidOp "Invalid test process status."

    let readStatus (stream: Stream) (target: TaskCompletionSource<int>) =
        task {
            try
                let bytes = Array.zeroCreate<byte> 1
                let mutable line = ""
                let mutable stage = 0
                let mutable receiving = true

                while receiving do
                    let! count = stream.ReadAsync(bytes.AsMemory())

                    if count = 0 then
                        receiving <- false
                    elif bytes[0] = 10uy then
                        stage <- advance target stage line
                        line <- ""
                    elif bytes[0] >= 32uy && bytes[0] <= 126uy && line.Length < 8 then
                        line <- line + string (char bytes[0])
                    else
                        invalidOp "Invalid test process status."

                if stage <> 3 || line <> "" then
                    invalidOp "Incomplete test process status."

                target.TrySetException(
                    InvalidOperationException("Test process target was stopped.")
                )
                |> ignore
            with error ->
                target.TrySetException error |> ignore
                return raise error
        }

    let observe helper group (milliseconds: int) =
        let timer = Stopwatch.StartNew()

        let remaining () =
            max 0 (milliseconds - int timer.ElapsedMilliseconds)

        let start = ProcessStartInfo(helper, UseShellExecute = false)

        [ "--observe"; string group; string milliseconds ]
        |> List.iter start.ArgumentList.Add

        use observer = new Process(StartInfo = start)

        if not (observer.Start()) then
            false
        elif observer.WaitForExit(remaining ()) then
            observer.ExitCode = 0 && timer.ElapsedMilliseconds <= int64 milliseconds
        else
            observer.Kill()
            observer.WaitForExit(remaining ()) |> ignore
            false

    let private stopGroup
        helper
        (control: AnonymousPipeServerStream)
        (child: Process)
        (protocol: Task)
        =
        let timer = Stopwatch.StartNew()

        let remaining () =
            max 1 (2000 - int timer.ElapsedMilliseconds)

        try
            control.Dispose()
            let exited = child.WaitForExit(remaining ())

            let confirmed =
                try
                    protocol.Wait(remaining ())
                with _ ->
                    false

            let absent =
                exited
                && timer.ElapsedMilliseconds < 2000L
                && observe helper child.Id (remaining ())

            exited
            && confirmed
            && child.ExitCode = 137
            && absent
            && timer.ElapsedMilliseconds < 2000L
        with _ ->
            false

    let private dispose (resource: IDisposable) =
        try
            resource.Dispose()
            true
        with _ ->
            false

    let private closeClients
        (control: AnonymousPipeServerStream)
        (status: AnonymousPipeServerStream)
        =
        [
            (fun () -> control.DisposeLocalCopyOfClientHandle())
            (fun () -> status.DisposeLocalCopyOfClientHandle())
        ]
        |> List.map (fun close ->
            try
                close ()
                true
            with _ ->
                false)
        |> List.forall id

    let private startupCleanup helper control status child started target =
        if started then
            try
                let closed = closeClients control status
                let protocol = readStatus status target
                let stopped = stopGroup helper control child protocol
                closed && stopped
            with _ ->
                dispose control |> ignore
                false
        else
            true

    let private launch
        helper
        start
        cancellation
        (control: AnonymousPipeServerStream)
        (status: AnonymousPipeServerStream)
        target
        =
        let mutable child: Process option = None
        let mutable started = false

        try
            let config =
                configuration
                    helper
                    start
                    (control.GetClientHandleAsString())
                    (status.GetClientHandleAsString())
                    cancellation

            let worker = new Process(StartInfo = config)
            child <- Some worker
            cancellation.ThrowIfCancellationRequested()
            started <- worker.Start()

            if not started then
                invalidOp "Test process supervisor could not start."

            if not (closeClients control status) then
                invalidOp "Test process control channels could not be handed over."

            worker, readStatus status target
        with error ->
            let prior =
                match error with
                | :? ProcessStartupFailure as failed -> failed.CleanupFailed
                | _ -> false

            let cleaned =
                child
                |> Option.forall (fun worker ->
                    startupCleanup helper control status worker started target)

            let closed =
                [ control :> IDisposable; status :> IDisposable ]
                |> List.map dispose
                |> List.forall id

            child |> Option.iter (fun worker -> dispose worker |> ignore)
            raise (ProcessStartupFailure(prior || not cleaned || not closed))

    type Session(helper, start, ?cancellationToken: CancellationToken) =
        let control =
            new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable)

        let status =
            try
                new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable)
            with _ ->
                control.Dispose()
                reraise ()

        let target =
            TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

        let supervisor, protocol =
            use startup = new CancellationTokenSource(2000)
            launch helper start (defaultArg cancellationToken startup.Token) control status target

        let mutable cleanup: bool option = None

        member _.Process = supervisor
        member _.Target = target.Task
        member _.CloseControl() = control.Dispose()
        member _.CloseStatus() = status.Dispose()

        member _.Stop() =
            match cleanup with
            | Some result -> result
            | None ->
                let result = stopGroup helper control supervisor protocol

                cleanup <- Some result
                result

        interface IDisposable with
            member this.Dispose() =
                this.Stop() |> ignore

                [ status :> IDisposable; control :> IDisposable; supervisor :> IDisposable ]
                |> List.iter (fun resource -> dispose resource |> ignore)
