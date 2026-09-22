namespace ClaimCore.Contracts

[<RequireQualifiedAccess>]
type WebHostFailure =
    | Input of HttpInputProblem
    | ConnectionRejected
    | OriginRejected
    | MediaTypeRejected
    | BodyTooLarge
    | SessionRejected
    | SessionForbidden
    | MethodRejected
    | AntiforgeryRejected
    | LoginRejected
    | EndpointMissing
    | Busy
    | ExportMetadataInvalid
    | BeforeDispatchFailed
    | DispatchUnconfirmed
    | CompletedResponseFailed

/// Only a typed host cause selects HTTP status and execution knowledge.
module WebHostFailures =
    let private group0 =
        [
            WebHostFailure.ConnectionRejected,
            ("WEB_HOST_CONNECTION_REJECTED",
             "WEB_CONNECTION_REJECTED",
             403,
             None,
             "Connection was refused.")
            WebHostFailure.OriginRejected,
            ("WEB_HOST_ORIGIN_REJECTED",
             "WEB_ORIGIN_REJECTED",
             403,
             None,
             "Request origin was refused.")
            WebHostFailure.MediaTypeRejected,
            ("WEB_HOST_MEDIA_TYPE_REJECTED",
             "WEB_MEDIA_TYPE",
             415,
             None,
             "The endpoint media type was refused.")
            WebHostFailure.BodyTooLarge,
            ("WEB_HOST_BODY_TOO_LARGE",
             "WEB_BODY_TOO_LARGE",
             413,
             None,
             "Request body is too large.")
            WebHostFailure.SessionRejected,
            ("WEB_HOST_SESSION_REJECTED", "WEB_SESSION_REJECTED", 401, None, "Session was refused.")
        ]

    let private group1 =
        [
            WebHostFailure.AntiforgeryRejected,
            ("WEB_HOST_ANTIFORGERY_REJECTED",
             "WEB_CSRF_REJECTED",
             403,
             None,
             "Request verification was refused.")
            WebHostFailure.LoginRejected,
            ("WEB_HOST_LOGIN_REJECTED",
             "WEB_LOGIN_REJECTED",
             401,
             Some "NOT_STARTED",
             "Login was refused.")
            WebHostFailure.EndpointMissing,
            ("WEB_HOST_ENDPOINT_MISSING", "WEB_NOT_FOUND", 404, None, "Endpoint was not found.")
            WebHostFailure.Busy,
            ("WEB_HOST_BUSY", "WEB_BUSY", 429, None, "Request admission is busy.")
            WebHostFailure.ExportMetadataInvalid,
            ("WEB_HOST_EXPORT_METADATA_INVALID",
             "WEB_PROTOCOL",
             500,
             None,
             "Recovery export metadata was invalid.")
        ]

    let private group2 =
        [
            WebHostFailure.BeforeDispatchFailed,
            ("WEB_HOST_BEFORE_DISPATCH_FAILED",
             "WEB_INTERNAL",
             500,
             Some "NOT_STARTED",
             "Request processing failed before dispatch.")
            WebHostFailure.DispatchUnconfirmed,
            ("WEB_HOST_DISPATCH_UNCONFIRMED",
             "WEB_INTERNAL",
             500,
             Some "STARTED_UNCONFIRMED",
             "The dispatched operation could not be confirmed. Preserve its exact identity.")
            WebHostFailure.CompletedResponseFailed,
            ("WEB_HOST_COMPLETED_RESPONSE_FAILED",
             "WEB_RESPONSE_FAILED",
             500,
             None,
             "Request processing completed but its response could not be delivered.")
        ]

    let private group3 =
        [
            WebHostFailure.SessionForbidden,
            ("WEB_HOST_SESSION_FORBIDDEN",
             "WEB_SESSION_REJECTED",
             403,
             None,
             "Session access was refused.")
            WebHostFailure.MethodRejected,
            ("WEB_HOST_METHOD_REJECTED",
             "WEB_METHOD_REJECTED",
             405,
             Some "NOT_STARTED",
             "The request method is not supported by this endpoint.")
        ]

    let private entries = group0 @ group1 @ group2 @ group3

    let all =
        (entries |> List.map fst)
        @ (HttpInputProblems.all |> List.map WebHostFailure.Input)

    let private policy reason =
        match reason with
        | WebHostFailure.Input problem ->
            let status, code, phase =
                if problem = HttpInputProblem.BodyTooLarge then
                    413, "WEB_BODY_TOO_LARGE", None
                else
                    400, "WEB_INVALID_REQUEST", Some "NOT_STARTED"

            HttpInputProblems.token problem, code, status, phase, HttpInputProblems.render problem
        | _ -> entries |> List.find (fst >> (=) reason) |> snd

    let token reason =
        let id, _, _, _, _ = policy reason in id

    let code reason =
        let _, code, _, _, _ = policy reason in code

    let status reason =
        let _, _, status, _, _ = policy reason in status

    let phase reason =
        let _, _, _, phase, _ = policy reason in phase

    let render reason =
        let _, _, _, _, message = policy reason in message

    let statuses = all |> List.map status |> List.distinct |> List.sort
