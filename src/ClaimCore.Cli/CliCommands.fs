namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

[<RequireQualifiedAccess>]
type CliCommand =
    | Help
    | Version
    | Schema
    | Call
    | Session
    | Shell
    | Recovery
    | Configure

[<RequireQualifiedAccess>]
type HelpTopic =
    | Overview
    | Help
    | Version
    | Schema
    | Call
    | Session
    | Shell
    | Recovery
    | Configure
    | Configuration
    | Delivery
    | Examples
    | Verbs
    | ExitCodes

[<RequireQualifiedAccess>]
type CliTopic =
    | Help of HelpTopic
    | Diagnostic of CliGuidance

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CliCommand =
    let vocabulary = Vocabulary.ofUnion<CliCommand> "command" TokenCasing.CamelCase None
    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

    let parse value =
        if value = "--version" then
            Some CliCommand.Version
        else
            Vocabulary.tryParse vocabulary value

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module HelpTopic =
    let vocabulary =
        Vocabulary.ofUnion<HelpTopic> "topic" TokenCasing.CamelCase None
        |> Vocabulary.entries
        |> List.map (fun (topic, token) ->
            topic, if topic = HelpTopic.ExitCodes then "exit-codes" else token)
        |> Vocabulary.ofEntries "topic"

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CliTopic =
    let vocabulary =
        [
            yield!
                Vocabulary.entries HelpTopic.vocabulary
                |> List.map (fun (topic, token) -> CliTopic.Help topic, token)
            yield!
                Vocabulary.entries CliGuidance.vocabulary
                |> List.map (fun (topic, token) -> CliTopic.Diagnostic topic, token)
        ]
        |> Vocabulary.ofEntries "topic"

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

module CliCommands =
    let aliases = [ "--help"; "-h" ]

    let commandTopic =
        function
        | CliCommand.Help -> CliTopic.Help HelpTopic.Help
        | CliCommand.Version -> CliTopic.Help HelpTopic.Version
        | CliCommand.Schema -> CliTopic.Help HelpTopic.Schema
        | CliCommand.Call -> CliTopic.Help HelpTopic.Call
        | CliCommand.Session -> CliTopic.Help HelpTopic.Session
        | CliCommand.Shell -> CliTopic.Help HelpTopic.Shell
        | CliCommand.Recovery -> CliTopic.Help HelpTopic.Recovery
        | CliCommand.Configure -> CliTopic.Help HelpTopic.Configure

    let private languageChoices = String.concat "|" LocalizationPolicy.languages

    /// Bootstrap syntax is host-owned; business operation syntax comes from OperationCatalog.
    let synopses =
        function
        | CliCommand.Schema -> [ "schema name"; "schema endpoint request|response" ]
        | CliCommand.Version -> [ "version [--json]"; "--version [--json]" ]
        | CliCommand.Help ->
            [
                "help [topic | family [verb...]] [--format text|json] [--language "
                + languageChoices
                + "]"
            ]
        | CliCommand.Call -> [ "call" ]
        | CliCommand.Session -> [ "session" ]
        | CliCommand.Shell -> [ "shell" ]
        | CliCommand.Recovery ->
            [
                "recovery list [--format text|json] [--language " + languageChoices + "]"
                "recovery check ATTEMPT_ID [--format text|json] [--language "
                + languageChoices
                + "]"
            ]
        | CliCommand.Configure ->
            [
                "configure --show"
                "configure --language " + languageChoices
                "configure --service-url URL --issuer URL --client-id ID --auth-mode interactive|automation [--service-trust-root-file PATH] [--oidc-trust-root-file PATH] [--language "
                + languageChoices
                + "]"
            ]

    let private protocolSynopses =
        [ "schema invocation"; "schema response"; "help call"; "help session" ]

    // Every closed HelpTopic has one syntax value; an added topic must remain a compile-time omission.
    let private helpSynopses =
        function
        | HelpTopic.Schema -> synopses CliCommand.Schema
        | HelpTopic.Version -> synopses CliCommand.Version
        | HelpTopic.Help -> synopses CliCommand.Help
        | HelpTopic.Call -> synopses CliCommand.Call @ protocolSynopses
        | HelpTopic.Session -> synopses CliCommand.Session @ protocolSynopses
        | HelpTopic.Shell -> synopses CliCommand.Shell
        | HelpTopic.Recovery -> synopses CliCommand.Recovery
        | HelpTopic.Configure -> synopses CliCommand.Configure
        | HelpTopic.Examples ->
            [ "help case open"; "case get REF"; "schema operation.execute request" ]
        | HelpTopic.ExitCodes -> [ "help exit-codes [--format text|json]" ]
        | HelpTopic.Overview
        | HelpTopic.Configuration
        | HelpTopic.Delivery
        | HelpTopic.Verbs -> []

    let topicSynopses =
        function
        | CliTopic.Help topic -> helpSynopses topic
        | CliTopic.Diagnostic CliGuidance.ProtocolGuidance -> protocolSynopses
        | CliTopic.Diagnostic CliGuidance.ConfigurationIssue -> []
