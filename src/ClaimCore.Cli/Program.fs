module ClaimCore.Cli.Program

open ClaimCore.Wire
open ClaimCore.Catalog

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

let private unsupported language issue =
    Console.Error.Write(ContractDiscovery.helpTopic language ("usage." + string issue))
    64

let private writeDiscovery language response =
    match response with
    | Some bytes ->
        writeBytes bytes
        0
    | None -> unsupported language CliUsageIssue.Selector

let private discovery =
    function
    | CliInvocation.Help(topic, language) ->
        Console.Out.Write(ContractDiscovery.helpTopic language topic)
        0
    | CliInvocation.Version false ->
        Console.Out.WriteLine(BuildIdentity.current.Version)
        0
    | CliInvocation.Version true ->
        Discovery.versionJson () |> writeBytes
        0
    | CliInvocation.Describe(arguments, false, _) ->
        Discovery.describe arguments |> writeDiscovery "en"
    | CliInvocation.Describe(arguments, true, language) ->
        Discovery.describeText language arguments |> writeDiscovery language
    | CliInvocation.Schema arguments -> Discovery.schema arguments |> writeDiscovery "en"
    | _ -> unsupported "en" CliUsageIssue.Invocation

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

        match CliInvocations.admit (Array.toList argv) with
        | Ok CliInvocation.Call -> remote false
        | Ok CliInvocation.Session -> remote true
        | Ok invocation -> discovery invocation
        | Error issue -> unsupported (CliInvocations.usageLanguage (Array.toList argv)) issue
    with _ ->
        try
            Console.Error.WriteLine((ContractDiscovery.message "cli.beforeDispatchFailure"))
        with _ ->
            ()

        70
