namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Protocol

[<RequireQualifiedAccess>]
type CliUsageIssue =
    | Invocation
    | Topic
    | Options
    | Selector

[<RequireQualifiedAccess>]
type CliCommand =
    | Help
    | Version
    | Describe
    | Schema
    | Call
    | Session

[<RequireQualifiedAccess>]
type HelpTopic =
    | Overview
    | Help
    | Version
    | Describe
    | Schema
    | Call
    | Session
    | Configuration
    | Delivery
    | Examples
    | Verbs

[<RequireQualifiedAccess>]
type CliTopic =
    | Help of HelpTopic
    | Diagnostic of CliGuidance

/// Ordinary help topics and diagnostic-code topics have distinct token conventions.
module CliCommands =
    let commandVocabulary =
        Vocabulary.ofUnion<CliCommand> "command" TokenCasing.CamelCase None

    let commands = Vocabulary.all commandVocabulary
    let commandToken = Vocabulary.token commandVocabulary
    let parseCommand = Vocabulary.tryParse commandVocabulary

    let private helpVocabulary =
        Vocabulary.ofUnion<HelpTopic> "topic" TokenCasing.CamelCase None

    let topicVocabulary =
        [
            yield!
                Vocabulary.entries helpVocabulary
                |> List.map (fun (topic, token) -> CliTopic.Help topic, token)
            yield!
                Vocabulary.entries CliGuidance.vocabulary
                |> List.map (fun (topic, token) -> CliTopic.Diagnostic topic, token)
        ]
        |> Vocabulary.ofEntries "topic"

    let topics = Vocabulary.all topicVocabulary
    let topicToken = Vocabulary.token topicVocabulary
    let parseTopic = Vocabulary.tryParse topicVocabulary
    let aliases = [ "--help"; "-h" ]

    let commandTopic =
        function
        | CliCommand.Help -> CliTopic.Help HelpTopic.Help
        | CliCommand.Version -> CliTopic.Help HelpTopic.Version
        | CliCommand.Describe -> CliTopic.Help HelpTopic.Describe
        | CliCommand.Schema -> CliTopic.Help HelpTopic.Schema
        | CliCommand.Call -> CliTopic.Help HelpTopic.Call
        | CliCommand.Session -> CliTopic.Help HelpTopic.Session

    /// Bootstrap syntax is host-owned; business operation syntax comes from OperationCatalog.
    let synopses =
        function
        | CliCommand.Describe ->
            [ "describe [family verb] [--format text|json] [--language en|lv|ar]" ]
        | CliCommand.Schema -> [ "schema name"; "schema endpoint request|response" ]
        | CliCommand.Version -> [ "version [--json]" ]
        | CliCommand.Help -> [ "help [topic] [--language en|lv|ar]" ]
        | CliCommand.Call -> [ "call" ]
        | CliCommand.Session -> [ "session" ]

    let private protocolSynopses =
        [ "schema invocation"; "schema response"; "help call"; "help session" ]

    let private helpSynopses =
        function
        | HelpTopic.Describe -> synopses CliCommand.Describe
        | HelpTopic.Schema -> synopses CliCommand.Schema
        | HelpTopic.Version -> synopses CliCommand.Version
        | HelpTopic.Help -> synopses CliCommand.Help
        | HelpTopic.Call -> synopses CliCommand.Call @ protocolSynopses
        | HelpTopic.Session -> synopses CliCommand.Session @ protocolSynopses
        | HelpTopic.Examples ->
            [ "describe case open"; "case get REF"; "schema operation.execute request" ]
        | HelpTopic.Overview
        | HelpTopic.Configuration
        | HelpTopic.Delivery
        | HelpTopic.Verbs -> []

    let topicSynopses =
        function
        | CliTopic.Help topic -> helpSynopses topic
        | CliTopic.Diagnostic CliGuidance.ProtocolGuidance -> protocolSynopses
        | CliTopic.Diagnostic CliGuidance.ConfigurationIssue -> []
