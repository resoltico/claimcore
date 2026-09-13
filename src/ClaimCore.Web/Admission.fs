namespace ClaimCore.Web

open System
open System.Net
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Http

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

module Admission =
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

    let private loopback (context: HttpContext) =
        context.Connection.RemoteIpAddress
        |> Option.ofObj
        |> Option.exists IPAddress.IsLoopback

    let trustedConnection origin context =
        exactHost origin context && loopback context

    let private matchingOrigin (origin: Uri) (context: HttpContext) =
        let expected = origin.GetLeftPart(UriPartial.Authority)

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

    let isCurrentSession (sessions: SessionRegistry) (context: HttpContext) =
        Security.sessionId context.User.Claims
        |> Option.exists (fun id -> sessions.IsCurrent(id, DateTimeOffset.UtcNow))

    let validatePost origin body maximumBytes antiforgery context =
        task {
            match postShape origin body maximumBytes context with
            | Error failure -> return Error failure
            | Ok() -> return! validateAntiforgery antiforgery context
        }

    let admitAuthenticated origin body maximumBytes sessions antiforgery context =
        task {
            match postShape origin body maximumBytes context with
            | Error failure -> return Error failure
            | Ok() when not (isCurrentSession sessions context) ->
                return Error AdmissionFailure.SessionRejected
            | Ok() -> return! validateAntiforgery antiforgery context
        }
