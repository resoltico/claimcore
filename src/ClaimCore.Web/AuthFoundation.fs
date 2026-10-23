namespace ClaimCore.Web

open ClaimCore.Contract

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
            then
                Ok uri
            else
                Error "OIDC_URI_INVALID"
        with :? UriFormatException ->
            Error "OIDC_URI_INVALID"

    let parse value = exactHttpsUri value

    let discoveryLocation (issuer: Uri) =
        Uri(issuer.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration")

    let validateMetadata (issuer: Uri) (source: ReadOnlyMemory<byte>) =
        try
            use document = JsonDocument.Parse(source)
            let root = document.RootElement

            let names =
                if root.ValueKind = JsonValueKind.Object then
                    root.EnumerateObject() |> Seq.map _.Name |> Seq.toList
                else
                    []

            let text (name: string) =
                match
                    root.ValueKind = JsonValueKind.Object,
                    names.Length = (names |> Set.ofList |> Set.count)
                with
                | true, true ->
                    match root.TryGetProperty(name) with
                    | true, property when property.ValueKind = JsonValueKind.String ->
                        property.GetString() |> Option.ofObj
                    | _ -> None
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
            ClaimCore.Web.Composition.AuthenticatedSubjects.human issuer.AbsoluteUri subject
        else
            Error "OIDC_SUBJECT_INVALID"

    let service (issuer: Uri) clientId =
        if nonBlank clientId then
            ClaimCore.Web.Composition.AuthenticatedSubjects.service issuer.AbsoluteUri clientId
        else
            Error "OIDC_CLIENT_INVALID"

    let private authenticatedIdentity (principal: ClaimsPrincipal) =
        match principal.Identities |> Seq.toList with
        | [ identity ] when identity.IsAuthenticated -> Some identity
        | _ -> None

    let private singleClaim (name: string) (identity: ClaimsIdentity) =
        match
            identity.Claims
            |> Seq.filter (fun claim -> String.Equals(claim.Type, name, StringComparison.Ordinal))
            |> Seq.toList
        with
        | [ claim ] -> Some claim.Value
        | _ -> None

    let fromBrowserSession issuer principal =
        match authenticatedIdentity principal |> Option.bind (singleClaim "sub") with
        | Some subject -> human issuer subject
        | None -> Error "OIDC_SUBJECT_INVALID"

    let private optionalClaim name (identity: ClaimsIdentity) =
        match
            identity.Claims
            |> Seq.filter (fun claim -> String.Equals(claim.Type, name, StringComparison.Ordinal))
            |> Seq.toList
        with
        | [] -> Ok None
        | [ claim ] when nonBlank claim.Value -> Ok(Some claim.Value)
        | _ -> Error "OIDC_CLAIM_AMBIGUOUS"

    let private clientIdentity identity =
        match optionalClaim "azp" identity, optionalClaim "client_id" identity with
        | Ok(Some first), Ok(Some second) when first = second -> Ok first
        | Ok(Some value), Ok None
        | Ok None, Ok(Some value) -> Ok value
        | _ -> Error "OIDC_CLIENT_INVALID"

    let fromAccessToken issuer cliClient principal =
        match authenticatedIdentity principal with
        | None -> Error "OIDC_PRINCIPAL_UNAUTHENTICATED"
        | Some identity ->
            match clientIdentity identity, optionalClaim "sub" identity with
            | Ok client, Ok(Some subject) when client = cliClient -> human issuer subject
            | Ok client, Ok _ when client <> cliClient -> service issuer client
            | _ -> Error "OIDC_PRINCIPAL_INVALID"
