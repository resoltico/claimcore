namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Protocol

[<RequireQualifiedAccess>]
type CliCommand =
    | Help
    | Version
    | Describe
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
    | Describe
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

[<RequireQualifiedAccess>]
type CliTopic =
    | Help of HelpTopic
    | Diagnostic of CliGuidance

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CliCommand =
    let vocabulary = Vocabulary.ofUnion<CliCommand> "command" TokenCasing.CamelCase None
    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module HelpTopic =
    let vocabulary = Vocabulary.ofUnion<HelpTopic> "topic" TokenCasing.CamelCase None

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
        | CliCommand.Describe -> CliTopic.Help HelpTopic.Describe
        | CliCommand.Schema -> CliTopic.Help HelpTopic.Schema
        | CliCommand.Call -> CliTopic.Help HelpTopic.Call
        | CliCommand.Session -> CliTopic.Help HelpTopic.Session
        | CliCommand.Shell -> CliTopic.Help HelpTopic.Shell
        | CliCommand.Recovery -> CliTopic.Help HelpTopic.Recovery
        | CliCommand.Configure -> CliTopic.Help HelpTopic.Configure

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
        | CliCommand.Shell -> [ "shell" ]
        | CliCommand.Recovery ->
            [
                "recovery list [--format text|json] [--language en|lv|ar]"
                "recovery check ATTEMPT_ID [--format text|json] [--language en|lv|ar]"
                "recovery settle ATTEMPT_ID [--format text|json] [--language en|lv|ar]"
            ]
        | CliCommand.Configure ->
            [
                "configure --show"
                "configure --service-url URL --issuer URL --client-id ID --auth-mode interactive|automation [--service-trust-root-file PATH] [--oidc-trust-root-file PATH]"
            ]

    let private protocolSynopses =
        [ "schema invocation"; "schema response"; "help call"; "help session" ]

    // Every closed HelpTopic has one syntax value; an added topic must remain a compile-time omission.
    // fsharplint:disable-next-line CyclomaticComplexity
    let private helpSynopses =
        function
        | HelpTopic.Describe -> synopses CliCommand.Describe
        | HelpTopic.Schema -> synopses CliCommand.Schema
        | HelpTopic.Version -> synopses CliCommand.Version
        | HelpTopic.Help -> synopses CliCommand.Help
        | HelpTopic.Call -> synopses CliCommand.Call @ protocolSynopses
        | HelpTopic.Session -> synopses CliCommand.Session @ protocolSynopses
        | HelpTopic.Shell -> synopses CliCommand.Shell
        | HelpTopic.Recovery -> synopses CliCommand.Recovery
        | HelpTopic.Configure -> synopses CliCommand.Configure
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
