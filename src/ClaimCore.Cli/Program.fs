module ClaimCore.Cli.Program

open System
open System.IO
open System.Text
open System.Threading
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Cli

let private writeBytes (bytes: byte array) =
    let output = Console.OpenStandardOutput()
    output.Write(bytes, 0, bytes.Length)
    output.Flush()

let private writeDiscovery (response: DiscoveryResponse) =
    match response with
    | DiscoveryResponse.Text text ->
        Console.Out.WriteLine(text)
        Console.Out.Flush()
    | DiscoveryResponse.Json bytes -> writeBytes bytes

let private writeProcessFailure reason =
    try
        let error = Console.OpenStandardError()
        error.Write(CliProcessDiagnostics.encode reason CliDeliveryPhase.Idle false None)
        error.Flush()
    with _ ->
        ()

let private unsupported () =
    writeProcessFailure CliProcessProblem.UnsupportedInvocation
    64

let private discovery arguments =
    match arguments with
    | []
    | [ "help" ] ->
        Discovery.help () |> writeDiscovery
        0
    | "help" :: _ ->
        Discovery.help () |> writeDiscovery
        0
    | [ "version" ] ->
        Console.Out.WriteLine(BuildIdentity.current.Version)
        Console.Out.Flush()
        0
    | [ "version"; "--json" ] ->
        Discovery.versionJson () |> writeBytes
        0
    | "describe" :: rest ->
        match Discovery.describe rest with
        | Ok response ->
            writeDiscovery response
            0
        | Error _ -> unsupported ()
    | "schema" :: rest ->
        match Discovery.schema rest with
        | Ok response ->
            writeDiscovery response
            0
        | Error _ -> unsupported ()
    | _ -> unsupported ()

let private dispatch (arguments: string list) =
    match arguments with
    | [ "call" ]
    | [ "session" ] ->
        use input = Console.OpenStandardInput()
        use output = Console.OpenStandardOutput()
        use errors = Console.OpenStandardError()
        use processor = new RemoteFrameProcessor(input, output, errors)

        let interrupt =
            ConsoleCancelEventHandler(fun _ event ->
                event.Cancel <- true
                Environment.Exit(processor.Interrupt()))

        Console.CancelKeyPress.AddHandler(interrupt)

        try
            if arguments = [ "call" ] then
                processor.Call(CancellationToken.None)
            else
                processor.Session(CancellationToken.None)
        finally
            Console.CancelKeyPress.RemoveHandler(interrupt)
    | _ -> discovery arguments

[<EntryPoint>]
let main argv =
    try
        BuildIdentity.requireCompatibleAssembly typeof<CliEndpoint>.Assembly
        Console.OutputEncoding <- UTF8Encoding(false)
        dispatch (Array.toList argv)
    with _ ->
        writeProcessFailure CliProcessProblem.UnexpectedFailure
        70
