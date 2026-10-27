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
    let private resolve = CliIdentity.programName + " operation resolve OPERATION_ID"

    let private overview language =
        let families =
            [
                "case", "cli.family.case"
                "authority", "cli.family.authority"
                "operation", "cli.family.operation"
                "context", "cli.family.context"
                "call", "cli.family.call"
                "session", "cli.family.session"
                "describe", "cli.family.describe"
                "schema", "cli.family.schema"
                "help", "cli.family.help"
            ]

        CliIdentity.programName
        + "\n"
        + (families
           |> List.map (fun (family, key) -> family + " — " + render language key Map.empty)
           |> String.concat "\n")
        + "\n"

    let private helpBody language =
        function
        | HelpTopic.Call ->
            renderCli language (CliMessage.HelpCall(invocation, request, response, retry, resolve))
        | HelpTopic.Session ->
            renderCli language (CliMessage.HelpSession(invocation, request, response))
        | HelpTopic.Configuration ->
            renderCli language (CliMessage.HelpConfiguration(secret, trust))
        | HelpTopic.Delivery -> renderCli language (CliMessage.HelpDelivery(retry, resolve))
        | HelpTopic.Describe -> render language "cli.help.describe" Map.empty
        | HelpTopic.Examples -> render language "cli.help.examples" Map.empty
        | HelpTopic.Help -> render language "cli.help.help" Map.empty
        | HelpTopic.Schema -> render language "cli.help.schema" Map.empty
        | HelpTopic.Version -> render language "cli.help.version" Map.empty
        | HelpTopic.Overview -> overview language
        | HelpTopic.Verbs ->
            Catalog.document.Entries
            |> List.map (fun entry ->
                CliIdentity.programName + " " + CommandGrammar.synopsis entry.Kind)
            |> String.concat "\n"
            |> fun value -> value + "\n"

    let private guidanceBody language =
        function
        | CliGuidance.ProtocolGuidance ->
            renderCli language (CliMessage.HelpProtocol(invocation, request, response))
        | CliGuidance.ConfigurationIssue ->
            renderCli language (CliMessage.HelpConfigurationIssue(secret, trust))

    let help language topic =
        let body =
            match topic with
            | CliTopic.Help help -> helpBody language help
            | CliTopic.Diagnostic guidance -> guidanceBody language guidance

        match topic with
        | CliTopic.Help(HelpTopic.Overview | HelpTopic.Verbs) -> body
        | CliTopic.Help _
        | CliTopic.Diagnostic _ -> renderCli language (CliMessage.HelpLayout body)
