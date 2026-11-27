namespace ClaimCore.Server

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Text.Json
open System.Security.Claims
open System.Globalization
open ClaimCore.Application

module OidcAuthority =
    let private httpsUri allowQuery (value: string) =
        try
            let uri = Uri(value, UriKind.Absolute)

            if
                uri.Scheme = Uri.UriSchemeHttps
                && uri.UserInfo = ""
                && (allowQuery || uri.Query = "")
                && uri.Fragment = ""
                && uri.HostNameType <> UriHostNameType.Unknown
            then
                Ok uri
            else
                Error AuthenticationFailure.UriInvalid
        with :? UriFormatException ->
            Error AuthenticationFailure.UriInvalid

    let parse value = httpsUri false value

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
                    |> List.forall (fun value -> Result.isOk (parse value)))
                ->
                match
                    text "end_session_endpoint" |> Option.bind (httpsUri true >> Result.toOption)
                with
                | Some endpoint -> Ok endpoint
                | None -> Error AuthenticationFailure.MetadataInvalid
            | _ -> Error AuthenticationFailure.MetadataInvalid
        with :? JsonException ->
            Error AuthenticationFailure.MetadataInvalid

module PrincipalIdentity =
    let private nonBlank (value: string) =
        not (String.IsNullOrWhiteSpace value) && value.Length <= 512

    let human (issuer: Uri) subject =
        if nonBlank subject then
            AuthenticatedPrincipals.human issuer.AbsoluteUri subject
        else
            Error AuthenticationFailure.SubjectInvalid

    let service (issuer: Uri) clientId =
        if nonBlank clientId then
            AuthenticatedPrincipals.service issuer.AbsoluteUri clientId
        else
            Error AuthenticationFailure.ClientInvalid

    let private authenticatedIdentity (principal: ClaimsPrincipal) =
        match principal.Identities |> Seq.toList with
        | [ identity ] when identity.IsAuthenticated -> Some identity
        | _ -> None

    let private uniqueClaim (name: string) (identity: ClaimsIdentity) =
        match
            identity.Claims
            |> Seq.filter (fun claim -> String.Equals(claim.Type, name, StringComparison.Ordinal))
            |> Seq.toList
        with
        | [ claim ] -> Some claim
        | _ -> None

    let private singleClaim name identity =
        uniqueClaim name identity |> Option.map _.Value

    let private claims (identity: ClaimsIdentity) =
        let emailVerified =
            match uniqueClaim "email_verified" identity with
            | Some claim when claim.ValueType = ClaimValueTypes.Boolean ->
                String.Equals(claim.Value, "true", StringComparison.OrdinalIgnoreCase)
            | _ -> false

        ClaimCore.Application.Contracts.RequesterClaims.admit
            (singleClaim "name" identity)
            (singleClaim "preferred_username" identity)
            (singleClaim "email" identity)
            emailVerified

    let fromBrowserSession issuer principal =
        match authenticatedIdentity principal with
        | Some identity ->
            match singleClaim "sub" identity with
            | Some subject ->
                human issuer subject
                |> Result.map (AuthenticatedPrincipals.withClaims (claims identity))
            | None -> Error AuthenticationFailure.SubjectInvalid
        | None -> Error AuthenticationFailure.SubjectInvalid

    let private optionalClaim name (identity: ClaimsIdentity) =
        match
            identity.Claims
            |> Seq.filter (fun claim -> String.Equals(claim.Type, name, StringComparison.Ordinal))
            |> Seq.toList
        with
        | [] -> Ok None
        | [ claim ] when nonBlank claim.Value -> Ok(Some claim.Value)
        | _ -> Error AuthenticationFailure.ClaimAmbiguous

    let private clientIdentity identity =
        match optionalClaim "azp" identity, optionalClaim "client_id" identity with
        | Ok(Some first), Ok(Some second) when first = second -> Some first
        | Ok(Some value), Ok None -> Some value
        | _ -> None

    let private instant name identity =
        let numeric =
            [
                ClaimValueTypes.Integer
                ClaimValueTypes.Integer32
                ClaimValueTypes.Integer64
            ]

        uniqueClaim name identity
        |> Option.bind (fun claim ->
            match Int64.TryParse(claim.Value, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, seconds when
                List.contains claim.ValueType numeric
                && seconds >= 0L
                && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
                ->
                Some(DateTimeOffset.FromUnixTimeSeconds seconds)
            | _ -> None)

    let private humanProof identity =
        match instant "iat" identity, instant "auth_time" identity with
        | Some issued, Some authenticated when authenticated <= issued ->
            Some
                {
                    IssuedAt = issued
                    AuthenticatedAt = authenticated
                }
        | _ -> None

    let browserAuthentication principal =
        authenticatedIdentity principal
        |> Option.bind (fun identity ->
            if singleClaim "typ" identity = Some "ID" then
                humanProof identity
            else
                None)

    let fromAccessToken issuer cliClient serviceClients principal =
        match authenticatedIdentity principal with
        | None -> Error AuthenticationFailure.PrincipalUnauthenticated
        | Some identity ->
            let admit principal proof =
                principal
                |> Result.map (
                    AuthenticatedPrincipals.withClaims (claims identity)
                    >> AuthenticatedPrincipals.withCredential proof
                )

            match
                singleClaim "typ" identity,
                clientIdentity identity,
                singleClaim "claimcore_principal" identity
            with
            | Some "Bearer", Some client, Some "human" when client = cliClient ->
                match singleClaim "sub" identity, humanProof identity with
                | Some subject, Some proof ->
                    admit (human issuer subject) (VerifiedCredential.HumanBearer proof)
                | _ -> Error AuthenticationFailure.PrincipalInvalid
            | Some "Bearer", Some client, Some "service" when Set.contains client serviceClients ->
                match instant "iat" identity with
                | Some issued ->
                    admit (service issuer client) (VerifiedCredential.MachineBearer issued)
                | None -> Error AuthenticationFailure.PrincipalInvalid
            | _ -> Error AuthenticationFailure.PrincipalInvalid
