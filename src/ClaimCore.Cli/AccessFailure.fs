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
        Vocabulary.define
            "AccessFailure"
            [
                AccessFailure.OidcBrowserUnavailable
                AccessFailure.OidcCallbackInvalid
                AccessFailure.OidcCredentialUnavailable
                AccessFailure.OidcGrantInvalid
                AccessFailure.OidcMetadataInvalid
                AccessFailure.OidcMetadataUnavailable
                AccessFailure.OidcResponseInvalid
                AccessFailure.OidcTokenEndpointInvalid
                AccessFailure.OidcTokenUnavailable
                AccessFailure.TlsTrustRootInvalid
                AccessFailure.TlsTrustRootScopeInvalid
                AccessFailure.TlsTrustRootUnavailable
            ]
            (function
             | AccessFailure.OidcBrowserUnavailable -> "OIDC_BROWSER_UNAVAILABLE"
             | AccessFailure.OidcCallbackInvalid -> "OIDC_CALLBACK_INVALID"
             | AccessFailure.OidcCredentialUnavailable -> "OIDC_CREDENTIAL_UNAVAILABLE"
             | AccessFailure.OidcGrantInvalid -> "OIDC_GRANT_INVALID"
             | AccessFailure.OidcMetadataInvalid -> "OIDC_METADATA_INVALID"
             | AccessFailure.OidcMetadataUnavailable -> "OIDC_METADATA_UNAVAILABLE"
             | AccessFailure.OidcResponseInvalid -> "OIDC_RESPONSE_INVALID"
             | AccessFailure.OidcTokenEndpointInvalid -> "OIDC_TOKEN_ENDPOINT_INVALID"
             | AccessFailure.OidcTokenUnavailable -> "OIDC_TOKEN_UNAVAILABLE"
             | AccessFailure.TlsTrustRootInvalid -> "TLS_TRUST_ROOT_INVALID"
             | AccessFailure.TlsTrustRootScopeInvalid -> "TLS_TRUST_ROOT_SCOPE_INVALID"
             | AccessFailure.TlsTrustRootUnavailable -> "TLS_TRUST_ROOT_UNAVAILABLE")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
