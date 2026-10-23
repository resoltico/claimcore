namespace ClaimCore.Contract

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

module InputDiagnostics =
    let configurationId = "CLI_CONFIGURATION_ISSUE"
    let localId = "CLI_LOCAL_FAILURE"
    let protocolId = "CLI_PROTOCOL_GUIDANCE"

    let settings =
        [
            CliSetting.ServiceUrl, "CLAIMCORE_SERVICE_URL"
            CliSetting.Issuer, "CLAIMCORE_OIDC_ISSUER"
            CliSetting.ClientId, "CLAIMCORE_OIDC_CLIENT_ID"
            CliSetting.AuthMode, "CLAIMCORE_CLI_AUTH_MODE"
            CliSetting.SecretFile, "CLAIMCORE_OIDC_CLIENT_SECRET_FILE"
            CliSetting.OidcTrustRoot, "CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE"
            CliSetting.ServiceTrustRoot, "CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE"
        ]

    let configurationIssues =
        [
            ConfigurationIssue.Missing, "MISSING"
            ConfigurationIssue.Invalid, "INVALID"
            ConfigurationIssue.PrivateInputRefused, "PRIVATE_INPUT_REFUSED"
        ]

/// Existing CLI admission/read bounds; help and boundary consumers share these exact values.
module CliConfigurationLimits =
    let MaximumClientIdCodeUnits = 256
    let MaximumSecretBytes = 4096
    let MaximumTrustRootBytes = 16384
