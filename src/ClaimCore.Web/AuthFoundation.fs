namespace ClaimCore.Web

open System
open System.Text.Json
open System.Security.Claims

module OidcAuthority =
    let private exactHttpsUri (value: string) =
        try
            let uri = Uri(value, UriKind.Absolute)

            if
                uri.Scheme = Uri.UriSchemeHttps
                && uri.UserInfo = ""
                && uri.Query = ""
                && uri.Fragment = ""
                && uri.HostNameType <> UriHostNameType.Unknown
                && not (value.EndsWith("/", StringComparison.Ordinal))
            then
                Ok uri
            else
                Error "OIDC_URI_INVALID"
        with :? UriFormatException ->
            Error "OIDC_URI_INVALID"

    let parse value = exactHttpsUri value

    let validateMetadata (issuer: Uri) (source: ReadOnlyMemory<byte>) =
        try
            use document = JsonDocument.Parse(source)
            let root = document.RootElement

            let text (name: string) =
                match root.TryGetProperty(name) with
                | true, property when property.ValueKind = JsonValueKind.String ->
                    property.GetString() |> Option.ofObj
                | _ -> None

            match
                text "issuer", text "authorization_endpoint", text "token_endpoint", text "jwks_uri"
            with
            | Some actual, Some authorization, Some token, Some jwks when
                String.Equals(actual, issuer.AbsoluteUri, StringComparison.Ordinal)
                && ([ authorization; token; jwks ]
                    |> List.forall (fun value -> Result.isOk (exactHttpsUri value)))
                ->
                Ok()
            | _ -> Error "OIDC_METADATA_INVALID"
        with :? JsonException ->
            Error "OIDC_METADATA_INVALID"

module PrincipalIdentity =
    let private nonBlank (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length <= 512

    let human (issuer: Uri) subject =
        if nonBlank subject then
            ClaimCore.Application.PrincipalKey.human issuer.AbsoluteUri subject
        else
            Error "OIDC_SUBJECT_INVALID"

    let service (issuer: Uri) clientId =
        if nonBlank clientId then
            ClaimCore.Application.PrincipalKey.service issuer.AbsoluteUri clientId
        else
            Error "OIDC_CLIENT_INVALID"

    let fromAccessToken issuer cliClient serviceClient (principal: ClaimsPrincipal) =
        let claim (name: string) =
            principal.FindFirst(name)
            |> Option.ofObj
            |> Option.map _.Value
            |> Option.defaultValue ""

        if not (principal.Identity |> Option.ofObj |> Option.exists _.IsAuthenticated) then
            Error "OIDC_PRINCIPAL_UNAUTHENTICATED"
        else
            let client = claim "azp"

            if client = serviceClient then service issuer client
            elif client = cliClient then human issuer (claim "sub")
            else Error "OIDC_CLIENT_UNKNOWN"
