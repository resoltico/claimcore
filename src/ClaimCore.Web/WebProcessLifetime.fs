namespace ClaimCore.Web

open System
open System.Runtime.InteropServices
open System.Threading

/// Stop is registered before startup I/O; signal callbacks never run request callbacks inline.
[<Sealed>]
type internal WebProcessLifetime() =
    let cancellation = new CancellationTokenSource()

    let stop () =
        try
            cancellation.CancelAsync() |> ignore
        with :? ObjectDisposedException ->
            ()

    let interrupt =
        ConsoleCancelEventHandler(fun _ event ->
            event.Cancel <- true
            stop ())

    let registrations =
        if OperatingSystem.IsWindows() then
            []
        else
            [ PosixSignal.SIGTERM; PosixSignal.SIGINT ]
            |> List.map (fun signal ->
                PosixSignalRegistration.Create(
                    signal,
                    Action<PosixSignalContext>(fun context ->
                        context.Cancel <- true
                        stop ())
                ))

    do Console.CancelKeyPress.AddHandler(interrupt)

    member _.Token = cancellation.Token

    interface IDisposable with
        member _.Dispose() =
            Console.CancelKeyPress.RemoveHandler(interrupt)
            registrations |> List.iter _.Dispose()
            cancellation.Dispose()
