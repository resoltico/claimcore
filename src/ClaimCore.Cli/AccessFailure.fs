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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module AccessFailure =
    let vocabulary =
        Vocabulary.ofUnion<AccessFailure> "AccessFailure" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
