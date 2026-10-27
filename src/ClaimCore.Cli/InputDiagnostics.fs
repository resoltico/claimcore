namespace ClaimCore.Cli

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type CliGuidance =
    | ProtocolGuidance
    | ConfigurationIssue

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CliGuidance =
    let vocabulary =
        Vocabulary.ofUnion<CliGuidance> "guidance" TokenCasing.ScreamingSnake (Some "CLI_")

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type CliSetting =
    | ServiceUrl
    | Issuer
    | ClientId
    | AuthMode
    | SecretFile
    | OidcTrustRoot
    | ServiceTrustRoot

[<RequireQualifiedAccess>]
type ConfigurationIssue =
    | Missing
    | Invalid
    | PrivateInputRefused

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CliSetting =
    let vocabulary = Vocabulary.ofUnion<CliSetting> "setting" TokenCasing.CamelCase None

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ConfigurationIssue =
    let vocabulary =
        Vocabulary.ofUnion<ConfigurationIssue> "issue" TokenCasing.ScreamingSnake None

    let all = Vocabulary.entries vocabulary
    let token = Vocabulary.token vocabulary

module InputDiagnostics =
    let configurationId = CliGuidance.token CliGuidance.ConfigurationIssue
    let localId = "CLI_LOCAL_FAILURE"
    let protocolId = CliGuidance.token CliGuidance.ProtocolGuidance

    // These are external environment-variable bindings, not case-name tokens.
    let settingName =
        function
        | CliSetting.ServiceUrl -> "CLAIMCORE_SERVICE_URL"
        | CliSetting.Issuer -> "CLAIMCORE_OIDC_ISSUER"
        | CliSetting.ClientId -> "CLAIMCORE_OIDC_CLIENT_ID"
        | CliSetting.AuthMode -> "CLAIMCORE_CLI_AUTH_MODE"
        | CliSetting.SecretFile -> "CLAIMCORE_OIDC_CLIENT_SECRET_FILE"
        | CliSetting.OidcTrustRoot -> "CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE"
        | CliSetting.ServiceTrustRoot -> "CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE"

    let settings =
        Vocabulary.all CliSetting.vocabulary
        |> List.map (fun setting -> setting, settingName setting)

/// Existing CLI admission/read bounds; help and boundary consumers share these exact values.
module CliConfigurationLimits =
    let MaximumClientIdCodeUnits = 256
    let MaximumSecretBytes = 4096
    let MaximumTrustRootBytes = 16384
