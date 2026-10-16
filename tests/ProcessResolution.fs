namespace ClaimCore.TestSupport

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

type ProcessStartupFailure(cleanupFailed: bool) =
    inherit Exception("Test process supervisor is unavailable or unsupported.")
    member _.CleanupFailed = cleanupFailed

/// Resolves only a test executable, under the controller's original environment and directory.
module ProcessResolution =
    let private read maximum (stream: Stream) (deadline: CancellationTokenSource) =
        task {
            try
                let bytes = Array.zeroCreate<byte>(maximum + 1)
                let mutable count = 0
                let mutable receiving = true

                while receiving && count < bytes.Length do
                    let! received = stream.ReadAsync(bytes.AsMemory(count), deadline.Token)
                    receiving <- received <> 0
                    count <- count + received

                if count > maximum then
                    invalidOp "Test executable resolution exceeded its allowance."

                return Array.take count bytes
            with error ->
                deadline.Cancel()
                return raise error
        }

    let private configuration helper name executableDirectory =
        let start =
            ProcessStartInfo(
                helper,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            )

        [ "--resolve"; name; executableDirectory ] |> List.iter start.ArgumentList.Add
        start

    let private reap (child: Process) (readers: Task array) =
        let timer = Stopwatch.StartNew()

        let remaining () =
            max 1 (2000 - int timer.ElapsedMilliseconds)

        try
            if not child.HasExited then
                child.Kill()

            let exited = child.WaitForExit(remaining ())

            try
                Task.WhenAll(readers).Wait(remaining ()) |> ignore
            with _ ->
                ()

            exited
            && Array.forall (fun (reader: Task) -> reader.IsCompleted) readers
            && timer.ElapsedMilliseconds < 2000L
        with _ ->
            false

    let resolve helper name executableDirectory (cancellation: CancellationToken) =
        use deadline = CancellationTokenSource.CreateLinkedTokenSource cancellation
        use child = new Process(StartInfo = configuration helper name executableDirectory)
        let mutable started = false
        let mutable readers = [||]

        try
            deadline.Token.ThrowIfCancellationRequested()
            started <- child.Start()

            if not started then
                invalidOp "Test executable resolver did not start."

            let output = read 4096 child.StandardOutput.BaseStream deadline
            let errors = read 64 child.StandardError.BaseStream deadline
            readers <- [| output :> Task; errors :> Task |]
            let exited = child.WaitForExitAsync(deadline.Token)
            Task.WhenAll(Array.append readers [| exited |]).GetAwaiter().GetResult()

            if child.ExitCode <> 0 || errors.Result.Length <> 0 || output.Result.Length = 0 then
                invalidOp "Test executable could not be resolved."

            UTF8Encoding(false, true).GetString output.Result
        with _ ->
            deadline.Cancel()
            let cleaned = not started || reap child readers
            raise (ProcessStartupFailure(not cleaned))
