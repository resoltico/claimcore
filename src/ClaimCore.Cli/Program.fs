module ClaimCore.Cli.Program

open ClaimCore.Wire

open System
open System.Text
open System.Threading
open System.Runtime.InteropServices

let private writeBytes (bytes: byte array) =
    let output = Console.OpenStandardOutput()
    output.Write bytes

    if bytes.Length = 0 || bytes[bytes.Length - 1] <> byte '\n' then
        output.WriteByte(byte '\n')

    output.Flush()

let private unsupported () =
    Console.Error.WriteLine("Unsupported invocation. Run claimcore help.")
    64

let private writeDiscovery response =
    match response with
    | Some bytes ->
        writeBytes bytes
        0
    | None -> unsupported ()

let private discovery =
    function
    | []
    | [ "help" ] ->
        Console.Out.WriteLine(Discovery.help ())
        0
    | [ "version" ] ->
        Console.Out.WriteLine(BuildIdentity.current.Version)
        0
    | [ "version"; "--json" ] ->
        Discovery.versionJson () |> writeBytes
        0
    | "describe" :: arguments -> Discovery.describe arguments |> writeDiscovery
    | "schema" :: arguments -> Discovery.schema arguments |> writeDiscovery
    | _ -> unsupported ()

let private remote session =
    use input = Console.OpenStandardInput()
    use output = Console.OpenStandardOutput()
    use errors = Console.OpenStandardError()
    use processor = new RemoteFrameProcessor(input, output, errors)
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
        if session then
            processor.Session CancellationToken.None
        else
            processor.Call CancellationToken.None
    finally
        Console.CancelKeyPress.RemoveHandler interrupt
        registrations |> List.iter _.Dispose()

[<EntryPoint>]
let main argv =
    try
        Console.OutputEncoding <- UTF8Encoding(false)

        match Array.toList argv with
        | [ "call" ] -> remote false
        | [ "session" ] -> remote true
        | arguments -> discovery arguments
    with _ ->
        try
            Console.Error.WriteLine("Unexpected failure before dispatch.")
        with _ ->
            ()

        70
