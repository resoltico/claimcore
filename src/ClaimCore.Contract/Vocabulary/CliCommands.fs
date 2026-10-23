namespace ClaimCore.Contract

[<RequireQualifiedAccess>]
type CliUsageIssue =
    | Invocation
    | Topic
    | Options
    | Selector

/// Offline command and topic identifiers; authored prose lives in the locale catalogs.
module CliCommands =
    let diagnosticRoutes =
        [
            InputDiagnostics.protocolId, InputDiagnostics.protocolId
            InputDiagnostics.configurationId, "configuration"
        ]

    let diagnosticTopics = diagnosticRoutes |> List.map fst

    let topics =
        [
            "overview"
            "help"
            "version"
            "describe"
            "schema"
            "call"
            "session"
            "configuration"
            "delivery"
            "examples"
        ]
        @ diagnosticTopics

    let aliases = [ "--help"; "-h" ]

    let commands = [ "help"; "version"; "describe"; "schema"; "call"; "session" ]
