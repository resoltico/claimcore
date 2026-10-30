namespace ClaimCore.Cli

open System
open System.IO
open System.Runtime.InteropServices
open ClaimCore.Domain
open ClaimCore.Protocol

/// Terminal and signal ownership stay outside the injected sequential shell session.
module internal ShellRuntime =
    let private refused output failure =
        let reply = CliReply.local failure
        CliDiscovery.writeBytes output reply.Bytes
        reply.ExitCode

    let private configured () =
        if Console.IsInputRedirected then
            Error ClientFailure.Configuration
        else
            RemoteConfiguration.load ()
            |> Result.bind (fun configuration ->
                match configuration.Mode with
                | RemoteClientMode.Interactive -> Ok configuration
                | RemoteClientMode.Automation -> Error ClientFailure.Configuration)

    let private signals (session: ShellSession) action =
        let interrupt =
            ConsoleCancelEventHandler(fun _ event ->
                event.Cancel <- true
                session.Interrupt())

        Console.CancelKeyPress.AddHandler interrupt

        let termination =
            if OperatingSystem.IsWindows() then
                None
            else
                Some(
                    PosixSignalRegistration.Create(
                        PosixSignal.SIGTERM,
                        Action<PosixSignalContext>(fun context ->
                            context.Cancel <- true
                            session.Stop())
                    )
                )

        try
            action ()
        finally
            Console.CancelKeyPress.RemoveHandler interrupt
            termination |> Option.iter _.Dispose()

    let run (output: Stream) (errors: Stream) =
        match configured () with
        | Error failure -> refused output failure
        | Ok configuration ->
            let streams =
                {
                    Input = Stream.Null
                    Output = output
                    Errors = errors
                }

            use processor = RemoteFrameProcessor.ForShell(streams, configuration)

            use terminal =
                new ShellTerminal(CliDiscovery.writeText errors, Limits.MaximumInvocationBytes)

            let console =
                {
                    Read = fun history prompt -> terminal.Read(history, prompt)
                    Interrupt = terminal.Interrupt
                    Output = output
                    Errors = errors
                    ReviewInteractive = not Console.IsOutputRedirected
                }

            let session = ShellSession(TimeProvider.System, console, processor)

            try
                signals session (fun () -> session.Run().GetAwaiter().GetResult())
            with error ->
                processor.Failure error
