namespace ClaimCore.Cli

open System
open System.Reflection
open System.Text.Json
open ClaimCore.Contract

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

    let private helpBody language topic =
        let invocation = string Limits.MaximumInvocationBytes
        let request = string Limits.MaximumRequestBytes
        let response = string Limits.MaximumResponseBytes
        let secret = string CliConfigurationLimits.MaximumSecretBytes
        let trust = string CliConfigurationLimits.MaximumTrustRootBytes
        let retry = CliIdentity.programName + " call"
        let resolve = CliIdentity.programName + " operation resolve OPERATION_ID"

        match topic with
        | "call" ->
            renderCli language (CliMessage.HelpCall(invocation, request, response, retry, resolve))
        | "session" -> renderCli language (CliMessage.HelpSession(invocation, request, response))
        | "CLI_PROTOCOL_GUIDANCE" ->
            renderCli language (CliMessage.HelpProtocol(invocation, request, response))
        | "configuration" -> renderCli language (CliMessage.HelpConfiguration(secret, trust))
        | "CLI_CONFIGURATION_ISSUE" ->
            renderCli language (CliMessage.HelpConfigurationIssue(secret, trust))
        | "delivery" -> renderCli language (CliMessage.HelpDelivery(retry, resolve))
        | "describe" -> render language "cli.help.describe" Map.empty
        | "examples" -> render language "cli.help.examples" Map.empty
        | "help" -> render language "cli.help.help" Map.empty
        | "schema" -> render language "cli.help.schema" Map.empty
        | "version" -> render language "cli.help.version" Map.empty
        | _ -> invalidArg (nameof topic) "Unknown help topic."

    let help language (topic: string) =
        let topic =
            if topic.StartsWith("diagnostic.", StringComparison.Ordinal) then
                topic.Substring "diagnostic.".Length
            else
                topic

        let body = helpBody language topic
        renderCli language (CliMessage.HelpLayout body)
