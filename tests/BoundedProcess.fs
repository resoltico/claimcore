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

module BoundedProcess =
    let private read maximum (stream: Stream) (deadline: CancellationTokenSource) =
        task {
            let buffer = Array.zeroCreate<byte>(maximum + 1)
            let mutable count = 0
            let mutable reading = true

            try
                while reading && count < buffer.Length do
                    let! received = stream.ReadAsync(buffer.AsMemory(count), deadline.Token)
                    reading <- received <> 0
                    count <- count + received

                if count > maximum then
                    invalidOp "Test process output exceeded its allowance."

                return Array.take count buffer
            with error ->
                deadline.Cancel()
                return raise error
        }

    /// Interactive fixtures retain their stdin/process ownership but bound each actual pipe reader.
    let readText maximum (reader: StreamReader) timeout =
        task {
            use deadline = new CancellationTokenSource(timeout: TimeSpan)
            let! bytes = read maximum reader.BaseStream deadline
            return System.Text.Encoding.UTF8.GetString(bytes)
        }

    let run (start: ProcessStartInfo) input maximum timeoutMilliseconds =
        if maximum < 1 || maximum >= Int32.MaxValue || timeoutMilliseconds < 1 then
            invalidArg (nameof maximum) "Test process bounds must be positive."

        use child = new Process(StartInfo = start)
        use deadline = new CancellationTokenSource(timeoutMilliseconds)

        if not (child.Start()) then
            invalidOp "Test process did not start."

        let stdout = read maximum child.StandardOutput.BaseStream deadline
        let stderr = read maximum child.StandardError.BaseStream deadline

        let writing =
            task {
                match input with
                | Some(bytes: byte array) ->
                    do! child.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(), deadline.Token)
                    child.StandardInput.Close()
                | None -> ()
            }

        let exited = child.WaitForExitAsync(deadline.Token)

        let completed =
            Task.WhenAll([| stdout :> Task; stderr :> Task; writing :> Task; exited |])

        try
            let winner =
                Task.WhenAny(completed, Task.Delay(timeoutMilliseconds)).GetAwaiter().GetResult()

            if not (obj.ReferenceEquals(winner, completed)) then
                invalidOp "Test process exceeded its deadline."

            completed.GetAwaiter().GetResult()

            {
                ExitCode = child.ExitCode
                StandardOutput = stdout.Result
                StandardError = stderr.Result
            }
        with _ ->
            deadline.Cancel()

            if not child.HasExited then
                child.Kill(true)
                child.WaitForExit(2000) |> ignore

            invalidOp "Test process exceeded its bounds or failed delivery."
