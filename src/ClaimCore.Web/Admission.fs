namespace ClaimCore.Web

open System
open System.Net
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Http
open ClaimCore.Application

[<RequireQualifiedAccess>]
type AdmissionFailure =
    | UntrustedConnection
    | OriginRejected
    | FetchMetadataRejected
    | UnsupportedMediaType
    | BodyTooLarge
    | SessionRejected
    | AntiforgeryRejected

[<RequireQualifiedAccess>]
type RequestBody =
    | Json
    | Raw of mediaType: string

[<RequireQualifiedAccess>]
type internal CredentialMode =
    | Missing
    | BrowserCookie
    | Bearer
    | Mixed

module Admission =
    let private oidcHuman (configuration: OidcConfiguration) (context: HttpContext) =
        task {
            let! authentication =
                context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme)

            if not authentication.Succeeded then
                return Error AdmissionFailure.SessionRejected
            else
                match authentication.Principal |> Option.ofObj with
                | None -> return Error AdmissionFailure.SessionRejected
                | Some principal ->
                    match PrincipalIdentity.fromBrowserSession configuration.Issuer principal with
                    | Ok actor -> return Ok actor
                    | Error _ -> return Error AdmissionFailure.SessionRejected
        }

    let bearerActor (configuration: OidcConfiguration) (context: HttpContext) =
        task {
            let! authentication = context.AuthenticateAsync(AuthMiddleware.BearerScheme)

            if not authentication.Succeeded then
                return Error AdmissionFailure.SessionRejected
            else
                match authentication.Principal |> Option.ofObj with
                | None -> return Error AdmissionFailure.SessionRejected
                | Some principal ->
                    match
                        PrincipalIdentity.fromAccessToken
                            configuration.Issuer
                            configuration.CliClientId
                            configuration.ServiceClientId
                            principal
                    with
                    | Ok actor -> return Ok actor
                    | Error _ -> return Error AdmissionFailure.SessionRejected
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
        | CredentialMode.Missing -> Task.FromResult(Error AdmissionFailure.SessionRejected)
        | CredentialMode.Bearer -> bearerActor configuration context
        | CredentialMode.BrowserCookie -> oidcHuman configuration context

    let private oneHeader name (context: HttpContext) =
        match context.Request.Headers.TryGetValue(name) with
        | true, values when values.Count = 1 && not (String.IsNullOrWhiteSpace(values[0])) ->
            Some values[0]
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
        | None -> true
        | Some supplied ->
            String.Equals(supplied, "same-origin", StringComparison.OrdinalIgnoreCase)

    let private contentType (expected: RequestBody) (context: HttpContext) =
        match context.Request.ContentType |> Option.ofObj with
        | None -> false
        | Some supplied ->
            let parts = supplied.Split(';', StringSplitOptions.None)
            let mediaType = (parts[0]).Trim()

            match expected with
            | RequestBody.Json ->
                String.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            | RequestBody.Raw expectedType ->
                parts.Length = 1
                && String.Equals(mediaType, expectedType, StringComparison.OrdinalIgnoreCase)

    let postShape origin body maximumBytes context =
        if not (trustedConnection origin context) then
            Error AdmissionFailure.UntrustedConnection
        elif not (matchingOrigin origin context) then
            Error AdmissionFailure.OriginRejected
        elif not (sameOriginFetch context) then
            Error AdmissionFailure.FetchMetadataRejected
        elif not (contentType body context) then
            Error AdmissionFailure.UnsupportedMediaType
        elif
            context.Request.ContentLength
            |> Option.ofNullable
            |> Option.exists (fun length -> length > int64 maximumBytes)
        then
            Error AdmissionFailure.BodyTooLarge
        else
            Ok()

    let validateAntiforgery (antiforgery: IAntiforgery) (context: HttpContext) =
        task {
            try
                do! antiforgery.ValidateRequestAsync(context)
                return Ok()
            with :? AntiforgeryValidationException ->
                return Error AdmissionFailure.AntiforgeryRejected
        }

    let validatePost origin body maximumBytes antiforgery context =
        match postShape origin body maximumBytes context with
        | Error failure -> Task.FromResult(Error failure)
        | Ok() -> validateAntiforgery antiforgery context

    let private bearerShape origin body maximumBytes (context: HttpContext) =
        if not (trustedConnection origin context) || not context.Request.IsHttps then
            Error AdmissionFailure.UntrustedConnection
        elif context.Request.Headers.ContainsKey("Origin") then
            Error AdmissionFailure.OriginRejected
        elif not (contentType body context) then
            Error AdmissionFailure.UnsupportedMediaType
        elif
            context.Request.ContentLength
            |> Option.ofNullable
            |> Option.exists (fun length -> length > int64 maximumBytes)
        then
            Error AdmissionFailure.BodyTooLarge
        else
            Ok()

    let actorGet configuration origin (context: HttpContext) =
        if not (trustedConnection origin context) || not context.Request.IsHttps then
            Task.FromResult(Error AdmissionFailure.UntrustedConnection)
        elif
            credentialMode context = CredentialMode.Bearer
            && context.Request.Headers.ContainsKey("Origin")
        then
            Task.FromResult(Error AdmissionFailure.OriginRejected)
        else
            verifiedPrincipal configuration context

    let private browserPost configuration origin body maximumBytes antiforgery context =
        task {
            match! validatePost origin body maximumBytes antiforgery context with
            | Error failure -> return Error failure
            | Ok() -> return! verifiedPrincipal configuration context
        }

    let actorPost configuration origin body maximumBytes antiforgery (context: HttpContext) =
        match credentialMode context with
        | CredentialMode.Missing
        | CredentialMode.Mixed -> Task.FromResult(Error AdmissionFailure.SessionRejected)
        | CredentialMode.Bearer ->
            match bearerShape origin body maximumBytes context with
            | Error failure -> Task.FromResult(Error failure)
            | Ok() -> verifiedPrincipal configuration context
        | CredentialMode.BrowserCookie ->
            if not context.Request.IsHttps then
                Task.FromResult(Error AdmissionFailure.UntrustedConnection)
            else
                browserPost configuration origin body maximumBytes antiforgery context
