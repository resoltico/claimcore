namespace ClaimCore.Web

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open ClaimCore.Contracts

module RouteSupport =
    let private dispatchKey = obj ()

    let markDispatched (context: HttpContext) = context.Items[dispatchKey] <- box false
    let markCompleted (context: HttpContext) = context.Items[dispatchKey] <- box true

    let hostFailure context reason =
        HttpHeaders.noStore context
        WebWire.hostFailure reason

    let admissionFailure context failure =
        let reason =
            match failure with
            | AdmissionFailure.UntrustedConnection -> WebHostFailure.ConnectionRejected
            | AdmissionFailure.OriginRejected
            | AdmissionFailure.FetchMetadataRejected -> WebHostFailure.OriginRejected
            | AdmissionFailure.UnsupportedMediaType -> WebHostFailure.MediaTypeRejected
            | AdmissionFailure.BodyTooLarge -> WebHostFailure.BodyTooLarge
            | AdmissionFailure.SessionRejected -> WebHostFailure.SessionRejected
            | AdmissionFailure.AntiforgeryRejected -> WebHostFailure.AntiforgeryRejected

        hostFailure context reason

    let inputFailure context reason =
        hostFailure context (WebHostFailure.Input reason)

    let admittedBody admit maximumBytes (context: HttpContext) =
        task {
            HttpHeaders.noStore context

            match! admit context with
            | Error failure -> return Error(admissionFailure context failure)
            | Ok() ->
                match! HttpInput.readBounded maximumBytes context.Request.Body with
                | Error reason -> return Error(inputFailure context reason)
                | Ok bytes -> return Ok bytes
        }

    let sourceDigest header (context: HttpContext) =
        match context.Request.Headers.TryGetValue(header) with
        | true, values when values.Count = 1 ->
            match values[0] |> string |> HttpInput.sourceDigest with
            | Ok value -> Ok value
            | Error reason -> Error(inputFailure context reason)
        | _ -> Error(inputFailure context HttpInputProblem.SourceDigestHeader)

    /// A partial response is aborted, never followed by a second JSON object. No exception text
    /// escapes. Dispatch knowledge is request-local and cannot be inferred from HTTP status.
    let handleFailures (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            try
                do! next.Invoke context

                if context.Response.StatusCode = 405 && not context.Response.HasStarted then
                    do! (hostFailure context WebHostFailure.MethodRejected).ExecuteAsync context
            with _ ->
                if context.Response.HasStarted then
                    context.Abort()
                else
                    let reason =
                        match context.Items.TryGetValue dispatchKey with
                        | true, (:? bool as completed) when completed ->
                            WebHostFailure.CompletedResponseFailed
                        | true, _ -> WebHostFailure.DispatchUnconfirmed
                        | _ -> WebHostFailure.BeforeDispatchFailed

                    try
                        context.Response.Clear()
                        do! (hostFailure context reason).ExecuteAsync context
                    with _ ->
                        context.Abort()
        }
