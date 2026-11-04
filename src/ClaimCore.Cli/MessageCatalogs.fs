namespace ClaimCore.Cli

open System
open System.Reflection
open System.Text.Json
open ClaimCore.Domain
open ClaimCore.Protocol

/// Resource I/O belongs to the client; catalog admission and rendering remain pure.
module internal MessageCatalogs =
    let private entries (assembly: Assembly) name =
        use stream = assembly.GetManifestResourceStream(name) |> nonNull
        use document = JsonDocument.Parse stream

        document.RootElement.EnumerateObject()
        |> Seq.map (fun item -> item.Name, item.Value.GetString() |> nonNull)
        |> Seq.toList

    let private catalogs =
        lazy
            let assembly = Assembly.GetExecutingAssembly()

            assembly.GetManifestResourceNames()
            |> Array.filter (fun name ->
                name.StartsWith("ClaimCore.Localization.", StringComparison.Ordinal))
            |> Array.groupBy (fun name -> name.Split('.')[2])
            |> Array.map (fun (language, names) ->
                let catalog =
                    names |> Array.toList |> List.collect (entries assembly) |> Messages.catalog

                match catalog with
                | Ok value -> language, value
                | Error error -> invalidOp error)
            |> Map.ofArray

    let private catalog language = Map.find language catalogs.Value

    let templates language = catalog language |> Messages.templates

    let render language key arguments =
        Messages.render (catalog language) key arguments

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

    let private invocation = string Limits.MaximumInvocationBytes
    let private request = string Limits.MaximumRequestBytes
    let private response = string Limits.MaximumResponseBytes
    let private secret = string CliConfigurationLimits.MaximumSecretBytes
    let private trust = string CliConfigurationLimits.MaximumTrustRootBytes
    let private retry = CliIdentity.programName + " call"

    let private status =
        CommandGrammar.commandText (CatalogOperation.Query QueryEndpoint.Status)
        + " OPERATION_ID"

    let private settlement =
        CommandGrammar.commandText (CatalogOperation.Query QueryEndpoint.Resolve)
        + " OPERATION_ID"

    let private overview language =
        let meaning =
            function
            | "case" -> CliMessage.CaseFamily
            | "work" -> CliMessage.WorkCommands
            | "reminder" -> CliMessage.ReminderCommands
            | "authority" -> CliMessage.AuthorityFamily
            | "operation" -> CliMessage.OperationFamily
            | "context" -> CliMessage.ContextFamily
            | name -> invalidOp ("Missing command-family meaning: " + name)

        let hostMeaning =
            function
            | CliCommand.Help -> CliMessage.HelpFamily
            | CliCommand.Version -> CliMessage.VersionFamily
            | CliCommand.Describe -> CliMessage.DescribeFamily
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
    // fsharplint:disable-next-line CyclomaticComplexity
    let private helpBody language =
        function
        | HelpTopic.Call ->
            renderCli
                language
                (CliMessage.HelpCall(invocation, request, response, retry, status, settlement))
        | HelpTopic.Session ->
            renderCli language (CliMessage.HelpSession(invocation, request, response))
        | HelpTopic.Configuration ->
            renderCli language (CliMessage.HelpConfiguration(secret, trust))
        | HelpTopic.Delivery ->
            renderCli language (CliMessage.HelpDelivery(retry, status, settlement))
        | HelpTopic.Describe -> renderCli language CliMessage.HelpDescribe
        | HelpTopic.Examples -> renderCli language CliMessage.HelpExamples
        | HelpTopic.Help -> renderCli language CliMessage.HelpHelp
        | HelpTopic.Schema -> renderCli language CliMessage.HelpSchema
        | HelpTopic.Version -> renderCli language CliMessage.HelpVersion
        | HelpTopic.Recovery -> renderCli language CliMessage.HelpRecovery
        | HelpTopic.Shell -> renderCli language CliMessage.HelpShell
        | HelpTopic.Configure -> renderCli language CliMessage.HelpConfigure
        | HelpTopic.Overview -> overview language
        | HelpTopic.Verbs ->
            Catalog.document.Entries
            |> List.map (fun entry ->
                CliIdentity.programName + " " + CommandGrammar.synopsis entry.Kind)
            |> String.concat "\n"
            |> fun value -> value + "\n\n" + renderCli language CliMessage.LiveTraversalHelp + "\n"

    let private guidanceBody language =
        function
        | CliGuidance.ProtocolGuidance ->
            renderCli language (CliMessage.HelpProtocol(invocation, request, response))
        | CliGuidance.ConfigurationIssue -> helpBody language HelpTopic.Configuration

    let help language topic =
        let body =
            match topic with
            | CliTopic.Help help -> helpBody language help
            | CliTopic.Diagnostic guidance -> guidanceBody language guidance

        match topic with
        | CliTopic.Help(HelpTopic.Overview | HelpTopic.Verbs) -> body
        | CliTopic.Help _
        | CliTopic.Diagnostic _ -> renderCli language (CliMessage.HelpLayout body)
