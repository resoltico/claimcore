namespace ClaimCore.Cli

open System
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

module UsageText =
    let private message language value =
        MessageCatalogs.renderCli language value

    let private choice language flag value allowed =
        if List.isEmpty allowed then
            message language (CliMessage.InvalidValue(flag, value))
        else
            message language (CliMessage.InvalidChoice(flag, value, String.concat "|" allowed))

    let private line language =
        function
        | UsageError.UnknownCommand(command, _) ->
            message language (CliMessage.UnknownCommand command)
        | UsageError.UnknownOption(flag, suggestion) ->
            message language (CliMessage.UnknownOption flag)
            + (suggestion
               |> Option.map (fun suggestion ->
                   " " + message language (CliMessage.Suggestion suggestion))
               |> Option.defaultValue "")
        | UsageError.MissingOption flag -> message language (CliMessage.MissingOption flag)
        | UsageError.MissingOperand operand -> message language (CliMessage.MissingOperand operand)
        | UsageError.ExtraOperand operand -> message language (CliMessage.ExtraOperand operand)
        | UsageError.InvalidValue(flag, value) ->
            message language (CliMessage.InvalidInteger(flag, value))
        | UsageError.InvalidRange(flag, value, minimum, maximum) ->
            message language (CliMessage.InvalidRange(flag, value, string minimum, string maximum))
        | UsageError.InvalidChoice(flag, value, allowed) -> choice language flag value allowed
        | UsageError.NotApplicable(flag, because) ->
            message language (CliMessage.NotApplicable(flag, because))
        | UsageError.DuplicateOption flag -> message language (CliMessage.DuplicateOption flag)
        | UsageError.MissingValue flag -> message language (CliMessage.MissingValue flag)
        | UsageError.RequestTooLarge maximum ->
            message language (CliMessage.RequestTooLarge(string maximum))

        | UsageError.UnknownTopic _ -> message language CliMessage.UnknownHelpTopic

    let render language arguments errors =
        let matched = CommandGrammar.tryOperation arguments

        let hints =
            match matched with
            | Some entry ->
                [
                    message
                        language
                        (CliMessage.Synopsis(
                            CliIdentity.programName
                            + " "
                            + CommandGrammar.synopsisForDraft
                                (List.contains "--dry-run" arguments)
                                entry
                        ))
                ]
            | None ->
                let suggestions =
                    errors
                    |> List.collect (function
                        | UsageError.UnknownCommand(_, suggestions) -> suggestions
                        | _ -> [])
                    |> List.distinct

                if List.isEmpty suggestions then
                    [ MessageCatalogs.help language (CliTopic.Help HelpTopic.Overview) ]
                else
                    suggestions
                    |> List.map (fun suggestion ->
                        message
                            language
                            (CliMessage.Suggestion(CliIdentity.programName + " " + suggestion)))

        let problem issue =
            match matched, issue with
            | Some entry, UsageError.UnknownOption(flag, suggestion) ->
                message
                    language
                    (CliMessage.UnknownOptionFor(
                        flag,
                        String.concat " " (CommandGrammar.words entry)
                    ))
                + (suggestion
                   |> Option.map (fun value -> " " + message language (CliMessage.Suggestion value))
                   |> Option.defaultValue "")
            | _ -> line language issue

        String.concat "\n" ((errors |> List.map (problem >> TerminalText.data)) @ hints)
        + "\n"

    let private host language arguments issue =
        let commandText = List.tryHead arguments |> Option.defaultValue "help"
        let command = CliCommand.parse commandText

        let problem =
            match command, issue with
            | Some CliCommand.Help, UsageError.UnknownTopic _ -> CliMessage.UnknownHelpTopic
            | Some CliCommand.Help, _ -> CliMessage.InvalidHelpOptions
            | Some CliCommand.Schema, UsageError.MissingOperand operand ->
                CliMessage.MissingOperand operand
            | Some CliCommand.Schema, UsageError.UnknownCommand _ ->
                CliMessage.InvalidSchemaSelector
            | Some CliCommand.Schema, _ -> CliMessage.InvalidSchemaOptions
            | Some CliCommand.Version, _ -> CliMessage.InvalidVersionOptions
            | Some CliCommand.Configure, _ -> CliMessage.InvalidConfigureOptions
            | _ -> CliMessage.InvalidInvocation commandText

        let hints =
            CliCommands.synopses (Option.defaultValue CliCommand.Help command)
            |> List.map (fun syntax ->
                message language (CliMessage.Synopsis(CliIdentity.programName + " " + syntax)))

        let hints =
            match command, issue with
            | Some CliCommand.Schema, UsageError.MissingOperand _ -> hints @ CliSchemas.names
            | _ -> hints

        let details =
            match command, issue with
            | Some CliCommand.Schema, UsageError.UnknownCommand _ ->
                [ TerminalText.data (line language issue) ]
            | _ -> []

        String.concat "\n" (TerminalText.data (message language problem) :: (details @ hints))
        + "\n"

    let renderHost language arguments issue =
        match issue with
        | UsageError.UnknownCommand _ when List.tryHead arguments = Some "schema" ->
            render language arguments [ issue ]
            + message language (CliMessage.Suggestion(CliIdentity.programName + " help schema"))
            + "\n"
        | UsageError.UnknownCommand _
        | UsageError.UnknownOption _ -> render language arguments [ issue ]
        | UsageError.UnknownTopic topic ->
            render language arguments [ UsageError.UnknownCommand(topic, []) ]
        | _ -> host language arguments issue
