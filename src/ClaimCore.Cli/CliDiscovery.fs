module internal ClaimCore.Cli.CliDiscovery

open System
open System.Text
open ClaimCore.Domain
open ClaimCore.Protocol

let writeBytes (output: System.IO.Stream) (bytes: byte array) =
    output.Write bytes

    if bytes.Length = 0 || bytes[bytes.Length - 1] <> byte '\n' then
        output.WriteByte(byte '\n')

    output.Flush()

let writeText (output: System.IO.Stream) (value: string) =
    output.Write(Encoding.UTF8.GetBytes value)
    output.Flush()

let unsupported errors language arguments issue =
    writeText errors (UsageText.renderHost language arguments issue)

    ExitCode.value ExitCode.Usage

let writeDiscovery output errors language command missing response =
    match response with
    | Some bytes ->
        writeBytes output bytes
        ExitCode.value ExitCode.Success
    | None -> unsupported errors (language ()) [ command ] missing

let discovery output errors =
    function
    | CliInvocation.Help(topic, language) ->
        writeText output (ContractDiscovery.helpTopic language topic)
        ExitCode.value ExitCode.Success
    | CliInvocation.CommandHelp(words, language) ->
        match Discovery.commandHelp language words with
        | Some text ->
            writeText output text
            ExitCode.value ExitCode.Success
        | None ->
            unsupported
                errors
                language
                words
                (UsageError.UnknownCommand(String.concat " " words, []))
    | CliInvocation.Version false ->
        writeText output (BuildIdentity.current.Version + Environment.NewLine)
        ExitCode.value ExitCode.Success
    | CliInvocation.Version true ->
        Discovery.versionJson () |> writeBytes output
        ExitCode.value ExitCode.Success
    | CliInvocation.Describe(selector, false, language) ->
        Discovery.describe selector
        |> writeDiscovery
            output
            errors
            (fun () -> language)
            "describe"
            (UsageError.UnknownCommand("describe", []))
    | CliInvocation.Describe(selector, true, language) ->
        Discovery.describeText language selector
        |> writeDiscovery
            output
            errors
            (fun () -> language)
            "describe"
            (UsageError.UnknownCommand("describe", []))
    | CliInvocation.Schema selection ->
        let selector =
            match selection with
            | CliSchema.Named name -> name
            | CliSchema.Request endpoint -> endpoint + " request"
            | CliSchema.Response endpoint -> endpoint + " response"

        Discovery.schema selection
        |> writeDiscovery
            output
            errors
            CliLanguage.effective
            "schema"
            (UsageError.UnknownCommand(selector, []))
    | _ -> unsupported errors "en" [] (UsageError.NotApplicable("arguments", "command"))
