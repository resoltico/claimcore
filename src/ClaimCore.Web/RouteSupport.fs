namespace ClaimCore.Web

open System.Threading.Tasks
open Microsoft.AspNetCore.Http

module RouteSupport =
    let hostFailure context status code message executionPhase =
        HttpHeaders.noStore context
        WebWire.hostFailure status code message executionPhase

    let admissionFailure context failure =
        match failure with
        | AdmissionFailure.UntrustedConnection ->
            hostFailure
                context
                StatusCodes.Status403Forbidden
                "WEB_CONNECTION_REJECTED"
                "Connection was refused."
                None
        | AdmissionFailure.OriginRejected
        | AdmissionFailure.FetchMetadataRejected ->
            hostFailure
                context
                StatusCodes.Status403Forbidden
                "WEB_ORIGIN_REJECTED"
                "Request origin was refused."
                None
        | AdmissionFailure.UnsupportedMediaType ->
            hostFailure
                context
                StatusCodes.Status415UnsupportedMediaType
                "WEB_MEDIA_TYPE"
                "The endpoint media type was refused."
                None
        | AdmissionFailure.BodyTooLarge ->
            hostFailure
                context
                StatusCodes.Status413PayloadTooLarge
                "WEB_BODY_TOO_LARGE"
                "Request body is too large."
                None
        | AdmissionFailure.SessionRejected ->
            hostFailure
                context
                StatusCodes.Status401Unauthorized
                "WEB_SESSION_REJECTED"
                "Session was refused."
                None
        | AdmissionFailure.AntiforgeryRejected ->
            hostFailure
                context
                StatusCodes.Status403Forbidden
                "WEB_CSRF_REJECTED"
                "Request verification was refused."
                None

    let inputFailure context message =
        if message = "The request exceeds the configured byte limit." then
            hostFailure
                context
                StatusCodes.Status413PayloadTooLarge
                "WEB_BODY_TOO_LARGE"
                "Request body is too large."
                None
        else
            hostFailure
                context
                StatusCodes.Status400BadRequest
                "WEB_INVALID_REQUEST"
                "Request input was refused."
                (Some "NOT_STARTED")

    let admittedBody admit maximumBytes (context: HttpContext) =
        task {
            HttpHeaders.noStore context

            match! admit context with
            | Error failure -> return Error(admissionFailure context failure)
            | Ok() ->
                match! HttpInput.readBounded maximumBytes context.Request.Body with
                | Error message -> return Error(inputFailure context message)
                | Ok bytes -> return Ok bytes
        }

    let sourceDigest header (context: HttpContext) =
        match context.Request.Headers.TryGetValue(header) with
        | true, values when values.Count = 1 ->
            match values[0] |> string |> HttpInput.sourceDigest with
            | Ok value -> Ok value
            | Error message -> Error(inputFailure context message)
        | _ -> Error(inputFailure context "A canonical source digest header is required.")
