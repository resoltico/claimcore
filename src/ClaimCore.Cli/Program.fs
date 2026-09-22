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

let private dispatch (stopped: CancellationToken) (arguments: string list) =
    match arguments with
    | [ "call" ]
    | [ "session" ] ->
        use supplier = new RuntimeSupplier()

        let run =
            if arguments = [ "call" ] then
                FrameProcessing.call
            else
                FrameProcessing.session

        run
            supplier
            (Console.OpenStandardInput())
            (Console.OpenStandardOutput())
            (Console.OpenStandardError())
            stopped
    | _ -> discovery arguments

[<EntryPoint>]
let main argv =
    use stopped = new CancellationTokenSource()

    Console.CancelKeyPress.Add(fun event ->
        event.Cancel <- true
        stopped.Cancel())

    try
        BuildIdentity.requireCompatibleAssembly typeof<Endpoint>.Assembly
        Console.OutputEncoding <- UTF8Encoding(false)
        dispatch stopped.Token (Array.toList argv)
    with _ ->
        writeProcessFailure CliProcessProblem.UnexpectedFailure
        70
