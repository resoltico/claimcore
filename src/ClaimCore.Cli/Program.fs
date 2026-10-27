module ClaimCore.Cli.Program

open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Json

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

let private unsupported errors language arguments issue =
    writeText errors (UsageText.renderHost language arguments issue)

    ExitCode.value ExitCode.Usage

let private writeDiscovery output errors language command response =
    match response with
    | Some bytes ->
        writeBytes output bytes
        ExitCode.value ExitCode.Success
    | None -> unsupported errors language [ command ] CliUsageIssue.Selector

let private discovery output errors =
    function
    | CliInvocation.Help(topic, language) ->
        writeText output (ContractDiscovery.helpTopic language topic)
        ExitCode.value ExitCode.Success
    | CliInvocation.Version false ->
        writeText output (BuildIdentity.current.Version + Environment.NewLine)
        ExitCode.value ExitCode.Success
    | CliInvocation.Version true ->
        Discovery.versionJson () |> writeBytes output
        ExitCode.value ExitCode.Success
    | CliInvocation.Describe(selector, false, _) ->
        Discovery.describe selector |> writeDiscovery output errors "en" "describe"
    | CliInvocation.Describe(selector, true, language) ->
        Discovery.describeText language selector
        |> writeDiscovery output errors language "describe"
    | CliInvocation.Schema selection ->
        Discovery.schema selection |> writeDiscovery output errors "en" "schema"
    | _ -> unsupported errors "en" [] CliUsageIssue.Invocation

let private remote input output errors session language render =
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
        if session then
            processor.Session CancellationToken.None
        else
            processor.Call CancellationToken.None
    finally
        Console.CancelKeyPress.RemoveHandler interrupt
        registrations |> List.iter _.Dispose()

let private verb output errors arguments =
    match Grammar.prepare (fun () -> Guid.NewGuid().ToString("D")) arguments with
    | Error issues ->
        let language = CliInvocations.usageLanguage arguments
        writeText errors (UsageText.render language arguments issues)
        ExitCode.value ExitCode.Usage
    | Ok prepared when prepared.DryRun ->
        writeBytes output prepared.Bytes
        ExitCode.value ExitCode.Success
    | Ok prepared ->
        use input = new System.IO.MemoryStream(prepared.Bytes, false)

        if prepared.Text then
            let invocation =
                Frames.admit prepared.Bytes
                |> Result.defaultWith (fun _ -> invalidOp "Invalid prepared request.")

            remote input output errors false prepared.Language (fun reply ->
                TextReply.bytesFor prepared.Language (Some invocation) reply.Frame)
        else
            remote input output errors false prepared.Language (fun reply -> reply.Bytes)

let private dispatch output errors argv =
    try
        match CliInvocations.admit (Array.toList argv) with
        | Ok CliInvocation.Call ->
            use input = Console.OpenStandardInput()
            remote input output errors false "en" (fun reply -> reply.Bytes)
        | Ok CliInvocation.Session ->
            use input = Console.OpenStandardInput()
            remote input output errors true "en" (fun reply -> reply.Bytes)
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
