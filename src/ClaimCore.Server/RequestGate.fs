namespace ClaimCore.Server

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain

open System
open System.Net
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Http

[<RequireQualifiedAccess>]
type GateFailure =
    | UntrustedConnection
    | OriginRejected
    | FetchMetadataRejected
    | UnsupportedMediaType
    | BodyTooLarge
    | SessionRejected
    | IdentityProviderUnavailable
    | IdentityProviderUntrusted
    | AntiforgeryRejected
    | AdmissionRejected

[<RequireQualifiedAccess>]
type internal CredentialMode =
    | Missing
    | BrowserCookie
    | Bearer
    | Mixed

module RequestGate =
    let private oidcHuman (configuration: OidcConfiguration) (context: HttpContext) =
        task {
            let! authentication =
                context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme)

            if not authentication.Succeeded then
                return Error GateFailure.SessionRejected
            else
                match authentication.Principal |> Option.ofObj with
                | None -> return Error GateFailure.SessionRejected
                | Some principal ->
                    match PrincipalIdentity.fromBrowserSession configuration.Issuer principal with
                    | Ok actor ->
                        return
                            authentication.Properties
                            |> Option.ofObj
                            |> Option.bind BrowserTicket.credential
                            |> Option.map (
                                ClaimCore.Application.VerifiedCredential.Browser
                                >> fun proof -> AuthenticatedPrincipals.withCredential proof actor
                            )
                            |> Option.defaultValue actor
                            |> Ok
                    | Error _ -> return Error GateFailure.SessionRejected
        }

    let bearerActor (configuration: OidcConfiguration) (context: HttpContext) =
        task {
            let! authentication = context.AuthenticateAsync(AuthMiddleware.BearerScheme)

            if not authentication.Succeeded then
                return
                    match OidcDependencyException.tryFind authentication.Failure with
                    | Some OidcDependencyFailure.Unavailable ->
                        Error GateFailure.IdentityProviderUnavailable
                    | Some OidcDependencyFailure.Untrusted ->
                        Error GateFailure.IdentityProviderUntrusted
                    | None -> Error GateFailure.SessionRejected
            else
                match authentication.Principal |> Option.ofObj with
                | None -> return Error GateFailure.SessionRejected
                | Some principal ->
                    match
                        PrincipalIdentity.fromAccessToken
                            configuration.Issuer
                            configuration.CliClientId
                            configuration.ServiceClientIds
                            principal
                    with
                    | Ok actor -> return Ok actor
                    | Error _ -> return Error GateFailure.SessionRejected
        }

    /// One credential mode per request. A failed bearer never falls back to a browser cookie.
    let internal credentialMode (context: HttpContext) =
        let suppliedBearer = context.Request.Headers.ContainsKey("Authorization")
        let suppliedCookie = context.Request.Cookies.ContainsKey("__Host-ClaimCoreSession")

        match suppliedBearer, suppliedCookie with
        | true, true -> CredentialMode.Mixed
        | true, false -> CredentialMode.Bearer
        | false, true -> CredentialMode.BrowserCookie
        | false, false -> CredentialMode.Missing

    let verifiedPrincipal (configuration: OidcConfiguration) (context: HttpContext) =
        match credentialMode context with
        | CredentialMode.Mixed
        | CredentialMode.Missing -> Task.FromResult(Error GateFailure.SessionRejected)
        | CredentialMode.Bearer -> bearerActor configuration context
        | CredentialMode.BrowserCookie -> oidcHuman configuration context

    let internal oneHeader name (context: HttpContext) =
        match context.Request.Headers.TryGetValue(name) with
        | true, values when values.Count = 1 ->
            Option.ofObj values[0] |> Option.filter (not << String.IsNullOrWhiteSpace)
        | _ -> None

    let private exactHost (origin: Uri) (context: HttpContext) =
        match oneHeader "Host" context with
        | Some supplied ->
            String.Equals(supplied, origin.Authority, StringComparison.OrdinalIgnoreCase)
        | None -> false

    let trustedConnection (endpoint: WebBinding) (context: HttpContext) =
        exactHost endpoint.Origin context
        && WebBindings.acceptsPeer endpoint context.Connection.RemoteIpAddress

    let private matchingOrigin (endpoint: WebBinding) (context: HttpContext) =
        let expected = endpoint.Origin.GetLeftPart(UriPartial.Authority)

        match oneHeader "Origin" context with
        | Some supplied -> String.Equals(supplied, expected, StringComparison.Ordinal)
        | None -> false

    let private sameOriginFetch (context: HttpContext) =
        match oneHeader "Sec-Fetch-Site" context with
        | None -> not (context.Request.Headers.ContainsKey "Sec-Fetch-Site")
        | Some supplied ->
            String.Equals(supplied, "same-origin", StringComparison.OrdinalIgnoreCase)

    let private contentType (context: HttpContext) =
        match context.Request.ContentType |> Option.ofObj with
        | None -> false
        | Some supplied ->
            let parts = supplied.Split(';', StringSplitOptions.None)
            let mediaType = (parts[0]).Trim()

            String.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)

    let private postShapeWith acceptsMedia origin maximumBytes context =
        if not (trustedConnection origin context) then
            Error GateFailure.UntrustedConnection
        elif not (matchingOrigin origin context) then
            Error GateFailure.OriginRejected
        elif not (sameOriginFetch context) then
            Error GateFailure.FetchMetadataRejected
        elif not (acceptsMedia context) then
            Error GateFailure.UnsupportedMediaType
        elif
            context.Request.ContentLength
            |> Option.ofNullable
            |> Option.exists (fun length -> length > int64 maximumBytes)
        then
            Error GateFailure.BodyTooLarge
        else
            Ok()

    let postShape origin maximumBytes context =
        postShapeWith contentType origin maximumBytes context

    let validateAntiforgery (antiforgery: IAntiforgery) (context: HttpContext) =
        task {
            try
                do! antiforgery.ValidateRequestAsync(context)
                return Ok()
            with :? AntiforgeryValidationException ->
                return Error GateFailure.AntiforgeryRejected
        }

    let private bearerShape acceptsMedia origin maximumBytes (context: HttpContext) =
        if not (trustedConnection origin context) || not context.Request.IsHttps then
            Error GateFailure.UntrustedConnection
        elif context.Request.Headers.ContainsKey("Origin") then
            Error GateFailure.OriginRejected
        elif not (acceptsMedia context) then
            Error GateFailure.UnsupportedMediaType
        elif
            context.Request.ContentLength
            |> Option.ofNullable
            |> Option.exists (fun length -> length > int64 maximumBytes)
        then
            Error GateFailure.BodyTooLarge
        else
            Ok()

    let actorGet configuration origin (context: HttpContext) =
        if not (trustedConnection origin context) || not context.Request.IsHttps then
            Task.FromResult(Error GateFailure.UntrustedConnection)
        elif
            credentialMode context = CredentialMode.Bearer
            && context.Request.Headers.ContainsKey("Origin")
        then
            Task.FromResult(Error GateFailure.OriginRejected)
        else
            verifiedPrincipal configuration context

    let internal antiforgeryHeader = "X-ClaimCore-Antiforgery"

    let private hasSingletonAntiforgery context =
        oneHeader antiforgeryHeader context
        |> Option.exists (fun value -> not (value.Contains ','))

    let private browserPost
        acceptsMedia
        headerOnly
        configuration
        origin
        maximumBytes
        antiforgery
        context
        =
        task {
            match postShapeWith acceptsMedia origin maximumBytes context with
            | Error reason -> return Error reason
            | Ok() when headerOnly && not (hasSingletonAntiforgery context) ->
                return Error GateFailure.AntiforgeryRejected
            | Ok() ->
                match! validateAntiforgery antiforgery context with
                | Error failure -> return Error failure
                | Ok() -> return! verifiedPrincipal configuration context
        }

    let private actorPostWith
        acceptsMedia
        headerOnly
        configuration
        origin
        maximumBytes
        antiforgery
        (context: HttpContext)
        =
        match credentialMode context with
        | CredentialMode.Missing
        | CredentialMode.Mixed -> Task.FromResult(Error GateFailure.SessionRejected)
        | CredentialMode.Bearer ->
            match bearerShape acceptsMedia origin maximumBytes context with
            | Error failure -> Task.FromResult(Error failure)
            | Ok() -> verifiedPrincipal configuration context
        | CredentialMode.BrowserCookie ->
            if not context.Request.IsHttps then
                Task.FromResult(Error GateFailure.UntrustedConnection)
            else
                browserPost
                    acceptsMedia
                    headerOnly
                    configuration
                    origin
                    maximumBytes
                    antiforgery
                    context

    let actorPost configuration origin maximumBytes antiforgery context =
        actorPostWith contentType false configuration origin maximumBytes antiforgery context

    let internal actorUpload acceptsMedia configuration origin maximumBytes antiforgery context =
        actorPostWith acceptsMedia true configuration origin maximumBytes antiforgery context
