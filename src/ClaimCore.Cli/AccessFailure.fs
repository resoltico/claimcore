namespace ClaimCore.Cli

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type AccessFailure =
    | OidcBrowserUnavailable
    | OidcCallbackInvalid
    | OidcCredentialUnavailable
    | OidcGrantInvalid
    | OidcMetadataInvalid
    | OidcMetadataUnavailable
    | OidcResponseInvalid
    | OidcTokenEndpointInvalid
    | OidcTokenUnavailable
    | TlsTrustRootInvalid
    | TlsTrustRootScopeInvalid
    | TlsTrustRootUnavailable

module AccessFailures =
    let vocabulary =
        Vocabulary.ofUnion<AccessFailure> "AccessFailure" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
