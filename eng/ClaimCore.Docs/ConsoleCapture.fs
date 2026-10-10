namespace ClaimCore.Docs

open System
open System.IO
open System.Threading

/// Retain raw bytes within one console allowance while continuously draining both pipes.
type internal ConsoleCapture(maximum: int) =
    let gate = obj ()
    let mutable retained = 0
    let mutable overflow = false

    member _.Overflow = lock gate (fun () -> overflow)

    member _.Read(stream: Stream, ct: CancellationToken) =
        task {
            use output = new MemoryStream()
            let buffer = Array.zeroCreate<byte> 8192
            let mutable reading = true

            while reading do
                let! received = stream.ReadAsync(buffer.AsMemory(), ct)
                reading <- received > 0

                let count =
                    lock gate (fun () ->
                        let admitted = min received (maximum - retained)
                        retained <- retained + admitted
                        overflow <- overflow || admitted < received
                        admitted)

                output.Write(buffer, 0, count)

            return output.ToArray()
        }
