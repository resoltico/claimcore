module ClaimCore.Cli.Program

open ClaimCore.Contract.Protocol
open ClaimCore.Contract

open System
open System.Text
open System.Threading
open System.Runtime.InteropServices

let private writeBytes (output: System.IO.Stream) (bytes: byte array) =
    output.Write bytes

    if bytes.Length = 0 || bytes[bytes.Length - 1] <> byte '\n' then
        output.WriteByte(byte '\n')

    output.Flush()

let private writeText (output: System.IO.Stream) (value: string) =
    output.Write(Encoding.UTF8.GetBytes value)
    output.Flush()

let private unsupported errors language issue =
    writeText errors (ContractDiscovery.helpTopic language ("usage." + string issue))
    64

let private writeDiscovery output errors language response =
    match response with
    | Some bytes ->
        writeBytes output bytes
        0
    | None -> unsupported errors language CliUsageIssue.Selector

let private discovery output errors =
    function
    | CliInvocation.Help(topic, language) ->
        writeText output (ContractDiscovery.helpTopic language topic)
        0
    | CliInvocation.Version false ->
        writeText output (BuildIdentity.current.Version + Environment.NewLine)
        0
    | CliInvocation.Version true ->
        Discovery.versionJson () |> writeBytes output
        0
    | CliInvocation.Describe(selector, false, _) ->
        Discovery.describe selector |> writeDiscovery output errors "en"
    | CliInvocation.Describe(selector, true, language) ->
        Discovery.describeText language selector
        |> writeDiscovery output errors language
    | CliInvocation.Schema selection ->
        Discovery.schema selection |> writeDiscovery output errors "en"
    | _ -> unsupported errors "en" CliUsageIssue.Invocation

let private remote input output errors session =
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

let private verb output errors arguments =
    let descriptors = ContractDiscovery.descriptorValue ()

    match Grammar.prepare (fun () -> Guid.NewGuid().ToString("D")) descriptors arguments with
    | Error() ->
        let language = CliInvocations.usageLanguage arguments
        writeText errors (ContractDiscovery.helpTopic language "verbs" + "\n")
        unsupported errors language CliUsageIssue.Invocation
    | Ok prepared when prepared.DryRun ->
        writeBytes output prepared.Bytes
        0
    | Ok prepared ->
        use input = new System.IO.MemoryStream(prepared.Bytes, false)

        if prepared.Text then
            let invocation =
                Frames.admit prepared.Bytes
                |> Result.defaultWith (fun _ -> invalidOp "Invalid prepared request.")

            use rendered = new TextReplySink(output, prepared.Language, invocation)
            remote input rendered errors false
        else
            remote input output errors false

let private dispatch output errors argv =
    try
        match CliInvocations.admit (Array.toList argv) with
        | Ok CliInvocation.Call ->
            use input = Console.OpenStandardInput()
            remote input output errors false
        | Ok CliInvocation.Session ->
            use input = Console.OpenStandardInput()
            remote input output errors true
        | Ok(CliInvocation.Verb arguments) -> verb output errors arguments
        | Ok invocation -> discovery output errors invocation
        | Error issue -> unsupported errors (CliInvocations.usageLanguage (Array.toList argv)) issue
    with _ ->
        try
            writeText
                errors
                (ContractDiscovery.message "cli.beforeDispatchFailure" + Environment.NewLine)
        with _ ->
            ()

        70

[<EntryPoint>]
let main argv =
    try
        use output = StandardSinks.output ()
        use errors = StandardSinks.errors ()
        dispatch output errors argv
    with _ ->
        70
