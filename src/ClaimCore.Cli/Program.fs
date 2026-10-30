module ClaimCore.Cli.Program

open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Json

open System
open System.Text
open System.Threading
open System.Runtime.InteropServices

open CliDiscovery

let private withRemote (streams: RemoteStreams) language render action =
    let input = streams.Input
    let output = streams.Output
    let errors = streams.Errors
    use processor = new RemoteFrameProcessor(input, output, errors, language, render)
    let stop () = Environment.Exit(processor.Interrupt())

    let interrupt =
        ConsoleCancelEventHandler(fun _ event ->
            event.Cancel <- true
            stop ())

    let registrations =
        if OperatingSystem.IsWindows() then
            []
        else
            [ PosixSignal.SIGINT; PosixSignal.SIGTERM ]
            |> List.map (fun signal ->
                PosixSignalRegistration.Create(
                    signal,
                    Action<PosixSignalContext>(fun context ->
                        context.Cancel <- true
                        stop ())
                ))

    Console.CancelKeyPress.AddHandler interrupt

    try
        try
            action processor
        with error ->
            processor.Failure error
    finally
        Console.CancelKeyPress.RemoveHandler interrupt
        registrations |> List.iter _.Dispose()

let private remote streams session language render =
    withRemote streams language render (fun processor ->
        if session then
            processor.Session CancellationToken.None
        else
            processor.Call CancellationToken.None)

let private verb (output: System.IO.Stream) (errors: System.IO.Stream) arguments =
    let console =
        {
            IsInteractive = not Console.IsInputRedirected && not Console.IsOutputRedirected
            ReadLine = fun () -> Console.ReadLine() |> Option.ofObj
            Write =
                fun bytes ->
                    output.Write(bytes: byte array)
                    output.Flush()
        }

    let io =
        {
            Output = output
            Errors = errors
            Review = console
        }

    withRemote
        {
            Input = System.IO.Stream.Null
            Output = output
            Errors = errors
        }
        (CliInvocations.usageLanguage arguments)
        (fun reply -> reply.Bytes)
        (fun processor ->
            VerbRunner.run io processor arguments CancellationToken.None
            |> _.GetAwaiter().GetResult())

let private configure output updates =
    let result =
        match updates with
        | None -> CliConfigure.show ()
        | Some values -> CliConfigure.save values |> Result.map (fun () -> [||])

    match result with
    | Ok bytes ->
        if bytes.Length > 0 then
            writeBytes output bytes

        ExitCode.value ExitCode.Success
    | Error reason ->
        let reply = CliReply.local reason
        writeBytes output reply.Bytes
        reply.ExitCode

let private dispatch output errors argv =
    try
        match CliInvocations.admit (Array.toList argv) with
        | Ok CliInvocation.Call ->
            use input = Console.OpenStandardInput()

            remote
                {
                    Input = input
                    Output = output
                    Errors = errors
                }
                false
                "en"
                (fun reply -> reply.Bytes)
        | Ok CliInvocation.Session ->
            use input = Console.OpenStandardInput()

            remote
                {
                    Input = input
                    Output = output
                    Errors = errors
                }
                true
                "en"
                (fun reply -> reply.Bytes)
        | Ok CliInvocation.Shell -> ShellRuntime.run output errors
        | Ok(CliInvocation.Configure updates) -> configure output updates
        | Ok(CliInvocation.Verb arguments) -> verb output errors arguments
        | Ok invocation -> discovery output errors invocation
        | Error issue ->
            unsupported
                errors
                (CliInvocations.usageLanguage (Array.toList argv))
                (Array.toList argv)
                issue
    with _ ->
        try
            writeText
                errors
                (ContractDiscovery.message (CliMessages.key CliMessage.BeforeDispatchFailure)
                 + Environment.NewLine)
        with _ ->
            ()

        ExitCode.value ExitCode.InternalError

[<EntryPoint>]
let main argv =
    try
        use output = StandardSinks.output ()
        use errors = StandardSinks.errors ()
        dispatch output errors argv
    with _ ->
        ExitCode.value ExitCode.InternalError
