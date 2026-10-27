namespace ClaimCore.Cli

open System
open ClaimCore.Domain
open ClaimCore.Protocol

module UsageText =
    let private message language value =
        MessageCatalogs.renderCli language value

    // Unrelated arbitrary input is not echoed as a command.
    let private suggestedCommandText command suggestions =
        if List.isEmpty suggestions then "" else command

    let private line language =
        function
        | UsageError.UnknownCommand(command, suggestions) ->
            message language (CliMessage.UnknownCommand(suggestedCommandText command suggestions))
        | UsageError.UnknownOption(flag, suggestion) ->
            message language (CliMessage.UnknownOption flag)
            + (suggestion
               |> Option.map (fun suggestion ->
                   " " + message language (CliMessage.Suggestion suggestion))
               |> Option.defaultValue "")
        | UsageError.MissingOption flag -> message language (CliMessage.MissingOption flag)
        | UsageError.MissingOperand operand -> message language (CliMessage.MissingOperand operand)
        | UsageError.ExtraOperand operand -> message language (CliMessage.ExtraOperand operand)
        | UsageError.InvalidInteger(flag, value) ->
            message language (CliMessage.InvalidInteger(flag, value))
        | UsageError.InvalidChoice(flag, value, []) ->
            message language (CliMessage.InvalidValue(flag, value))
        | UsageError.InvalidChoice(flag, value, allowed) ->
            message language (CliMessage.InvalidChoice(flag, value, String.concat "|" allowed))
        | UsageError.NotApplicable(flag, because) ->
            message language (CliMessage.NotApplicable(flag, because))
        | UsageError.DuplicateOption flag -> message language (CliMessage.DuplicateOption flag)
        | UsageError.MissingValue flag -> message language (CliMessage.MissingValue flag)
        | UsageError.RequestTooLarge maximum ->
            message language (CliMessage.RequestTooLarge(string maximum))

    let render language arguments errors =
        let matched =
            OperationCatalog.all
            |> List.tryFind (fun entry ->
                let words = OperationCatalog.words entry in
                List.truncate words.Length arguments = words)

        let hints =
            match matched with
            | Some entry ->
                [
                    message
                        language
                        (CliMessage.Synopsis(
                            CliIdentity.programName + " " + OperationCatalog.synopsis entry
                        ))
                ]
            | None ->
                errors
                |> List.collect (function
                    | UsageError.UnknownCommand(_, suggestions) -> suggestions
                    | _ -> [])
                |> List.distinct
                |> List.map (fun suggestion ->
                    message
                        language
                        (CliMessage.Suggestion(CliIdentity.programName + " " + suggestion)))

        String.concat "\n" ((errors |> List.map (line language)) @ hints) + "\n"

    let renderHost language arguments issue =
        let commandText = List.tryHead arguments |> Option.defaultValue "help"
        let command = CliCommand.parse commandText

        let problem =
            match command, issue with
            | Some CliCommand.Help, CliUsageIssue.Topic -> CliMessage.UnknownHelpTopic
            | Some CliCommand.Help, _ -> CliMessage.InvalidHelpOptions
            | Some CliCommand.Describe, CliUsageIssue.Selector -> CliMessage.InvalidDescribeSelector
            | Some CliCommand.Describe, _ -> CliMessage.InvalidDescribeOptions
            | Some CliCommand.Schema, CliUsageIssue.Selector -> CliMessage.InvalidSchemaSelector
            | Some CliCommand.Schema, _ -> CliMessage.InvalidSchemaOptions
            | Some CliCommand.Version, _ -> CliMessage.InvalidVersionOptions
            | _ -> CliMessage.InvalidInvocation commandText

        let hints =
            CliCommands.synopses (Option.defaultValue CliCommand.Help command)
            |> List.map (fun syntax ->
                message language (CliMessage.Synopsis(CliIdentity.programName + " " + syntax)))

        String.concat "\n" (message language problem :: hints) + "\n"
