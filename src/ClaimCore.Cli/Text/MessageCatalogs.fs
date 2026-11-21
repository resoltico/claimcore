namespace ClaimCore.Cli

open ClaimCore.CommandLine

open System
open System.Reflection
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

/// Resource I/O belongs to the client; catalog admission and rendering remain pure.
module internal MessageCatalogs =
    let internal catalogFrom language english translated =
        let entries, _ = LocalizationPolicy.load language english translated
        entries |> Map.toList |> Messages.catalog |> Result.defaultWith invalidOp

    let private catalogs =
        lazy
            (let assembly = Assembly.GetExecutingAssembly()

             let sources language =
                 assembly.GetManifestResourceNames()
                 |> Array.filter (fun name ->
                     name.StartsWith(
                         "ClaimCore.Localization." + language + ".",
                         StringComparison.Ordinal
                     ))
                 |> Array.map (fun name ->
                     use stream = assembly.GetManifestResourceStream(name) |> nonNull
                     use bytes = new System.IO.MemoryStream()
                     stream.CopyTo bytes
                     bytes.ToArray())
                 |> Array.toList

             let english = sources "en"

             LocalizationPolicy.languages
             |> List.map (fun language -> language, catalogFrom language english (sources language))
             |> Map.ofList)

    let private catalog language = Map.find language catalogs.Value

    let templates language = catalog language |> Messages.templates

    let renderWithNumber formatNumber language key arguments =
        let arguments =
            if key = "endpoint.operation.get.meaning" && Map.isEmpty arguments then
                let duration =
                    Messages.render
                        (catalog language)
                        "label.operationWindow"
                        (Map.ofList
                            [
                                "minutes",
                                formatNumber (OperationLifetime.WindowMilliseconds / 60000L)
                            ])

                Map.ofList [ "window", duration ]
            else
                arguments

        Messages.render (catalog language) key arguments

    let render language key arguments =
        renderWithNumber
            (fun (value: int64) -> value.ToString(Globalization.CultureInfo.InvariantCulture))
            language
            key
            arguments

    let template language key =
        catalog language
        |> Messages.templates
        |> Map.find key
        |> Template.parts
        |> List.map (function
            | TemplatePart.Literal text -> text
            | TemplatePart.Hole name -> "{" + name + "}")
        |> String.concat ""

    let renderProduced language (message: ProducedMessage) =
        render language message.Key message.Arguments

    let renderCli language message =
        renderProduced language (CliMessages.produce message)

    let private window formatNumber language =
        render
            language
            "label.operationWindow"
            (Map.ofList [ "minutes", formatNumber (OperationLifetime.WindowMilliseconds / 60000L) ])

    let private overview language =
        let meaning =
            function
            | "case" -> CliMessage.CaseFamily
            | "note" -> CliMessage.NoteCommands
            | "attachment" -> CliMessage.AttachmentCommands
            | "party" -> CliMessage.PartyCommands
            | "link" -> CliMessage.LinkCommands
            | "payment" -> CliMessage.PaymentCommands
            | "work" -> CliMessage.WorkCommands
            | "reminder" -> CliMessage.ReminderCommands
            | "session" -> CliMessage.BrowserSessions
            | "people" -> CliMessage.PeopleFamily
            | "disclosure" -> CliMessage.DisclosureFamily
            | "operation" -> CliMessage.OperationFamily
            | "context" -> CliMessage.ContextFamily
            | name -> invalidOp ("Missing command-family meaning: " + name)

        let hostMeaning =
            function
            | CliCommand.Help -> CliMessage.HelpFamily
            | CliCommand.Version -> CliMessage.VersionFamily
            | CliCommand.Schema -> CliMessage.SchemaFamily
            | CliCommand.Call -> CliMessage.CallFamily
            | CliCommand.Session -> CliMessage.SessionFamily
            | CliCommand.Shell -> CliMessage.ShellFamily
            | CliCommand.Recovery -> CliMessage.RecoveryFamily
            | CliCommand.Configure -> CliMessage.ConfigureFamily

        let families =
            (CommandGrammar.families |> List.map (fun name -> name, meaning name))
            @ (CliCommand.all
               |> List.map (fun command -> CliCommand.token command, hostMeaning command))

        CliIdentity.programName
        + "\n"
        + (families
           |> List.map (fun (family, key) -> family + " — " + renderCli language key)
           |> String.concat "\n")
        + "\n"
        + renderCli language (CliMessage.HelpCommands(CliIdentity.programName + " help <family>"))
        + "\n"

    // This total HelpTopic-to-text table has no nested decisions; keep every topic compiler-checked.
    let private helpBody formatNumber language =
        function
        | HelpTopic.Call -> renderCli language (CliMessage.HelpCall)
        | HelpTopic.Session -> renderCli language (CliMessage.HelpCall)
        | HelpTopic.Configuration -> renderCli language (CliMessage.HelpConfiguration)
        | HelpTopic.Delivery ->
            renderCli language (CliMessage.HelpDelivery(window formatNumber language))
        | HelpTopic.Examples -> renderCli language CliMessage.HelpExamples
        | HelpTopic.Help -> renderCli language CliMessage.HelpHelp
        | HelpTopic.Schema -> renderCli language CliMessage.HelpSchema
        | HelpTopic.Version -> renderCli language CliMessage.HelpVersion
        | HelpTopic.Recovery -> renderCli language CliMessage.HelpRecovery
        | HelpTopic.Shell -> renderCli language CliMessage.HelpShell
        | HelpTopic.Configure -> renderCli language CliMessage.HelpConfigure
        | HelpTopic.Overview -> overview language
        | HelpTopic.ExitCodes ->
            let value code = string (ExitCode.value code)

            renderCli
                language
                (CliMessage.HelpExitCodes(
                    value ExitCode.Success,
                    value ExitCode.Rejected,
                    value ExitCode.Failed,
                    value ExitCode.OutcomeUnknown,
                    value ExitCode.Usage,
                    value ExitCode.InternalError,
                    value ExitCode.Cancelled
                ))
        | HelpTopic.Verbs ->
            CommandGrammar.verbs
            |> List.map (fun operation ->
                CliIdentity.programName + " " + CommandGrammar.synopsis operation)
            |> String.concat "\n"
            |> fun value -> value + "\n\n" + renderCli language CliMessage.LiveTraversalHelp + "\n"

    let private guidanceBody formatNumber language =
        function
        | CliGuidance.ProtocolGuidance -> renderCli language (CliMessage.HelpCall)
        | CliGuidance.ConfigurationIssue -> helpBody formatNumber language HelpTopic.Configuration

    let helpWithNumber formatNumber language topic =
        let body =
            match topic with
            | CliTopic.Help help -> helpBody formatNumber language help
            | CliTopic.Diagnostic guidance -> guidanceBody formatNumber language guidance

        match topic with
        | CliTopic.Help(HelpTopic.Overview | HelpTopic.Verbs) -> body
        | CliTopic.Help _
        | CliTopic.Diagnostic _ -> renderCli language (CliMessage.HelpLayout body)

    let help language topic =
        helpWithNumber
            (fun (value: int64) -> value.ToString(Globalization.CultureInfo.InvariantCulture))
            language
            topic
