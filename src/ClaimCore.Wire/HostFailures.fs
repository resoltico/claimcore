namespace ClaimCore.Wire

[<RequireQualifiedAccess>]
type HostFailure =
    | ContractMismatch
    | ConnectionRejected
    | OriginRejected
    | MediaTypeRejected
    | BodyTooLarge
    | SessionRejected
    | SessionForbidden
    | MethodRejected
    | AntiforgeryRejected
    | EndpointMissing
    | RateLimited
    | InputInvalid
    | BeforeDispatchFailed
    | DispatchUnconfirmed
    | CompletedResponseFailed

type HostFailureReply =
    { Reason: HostFailure; Message: string }

module HostFailures =
    let private policies =
        [
            HostFailure.ContractMismatch,
            "CONTRACT_MISMATCH",
            409,
            Some ExecutionPhase.NotStarted,
            "The client contract differs. Reload or use a matching client."
            HostFailure.ConnectionRejected,
            "CONNECTION_REJECTED",
            403,
            Some ExecutionPhase.NotStarted,
            "Connection was refused."
            HostFailure.OriginRejected,
            "ORIGIN_REJECTED",
            403,
            Some ExecutionPhase.NotStarted,
            "Origin was refused."
            HostFailure.MediaTypeRejected,
            "MEDIA_TYPE_REJECTED",
            415,
            Some ExecutionPhase.NotStarted,
            "Use the endpoint media type."
            HostFailure.BodyTooLarge,
            "BODY_TOO_LARGE",
            413,
            Some ExecutionPhase.NotStarted,
            "Request body is too large."
            HostFailure.SessionRejected,
            "SESSION_REJECTED",
            401,
            Some ExecutionPhase.NotStarted,
            "Authentication was refused."
            HostFailure.SessionForbidden,
            "SESSION_FORBIDDEN",
            403,
            Some ExecutionPhase.NotStarted,
            "Session access was refused."
            HostFailure.MethodRejected,
            "METHOD_REJECTED",
            405,
            Some ExecutionPhase.NotStarted,
            "The method is unsupported."
            HostFailure.AntiforgeryRejected,
            "ANTIFORGERY_REJECTED",
            403,
            Some ExecutionPhase.NotStarted,
            "Request verification was refused."
            HostFailure.EndpointMissing,
            "ENDPOINT_MISSING",
            404,
            Some ExecutionPhase.NotStarted,
            "Endpoint was not found."
            HostFailure.RateLimited,
            "RATE_LIMITED",
            429,
            Some ExecutionPhase.NotStarted,
            "Request admission is busy."
            HostFailure.InputInvalid,
            "INPUT_INVALID",
            400,
            Some ExecutionPhase.NotStarted,
            "The input does not satisfy the endpoint contract."
            HostFailure.BeforeDispatchFailed,
            "INTERNAL",
            500,
            Some ExecutionPhase.NotStarted,
            "Processing failed before dispatch."
            HostFailure.DispatchUnconfirmed,
            "INTERNAL",
            500,
            Some ExecutionPhase.StartedUnconfirmed,
            "Preserve the exact request and replay it."
            HostFailure.CompletedResponseFailed,
            "RESPONSE_FAILED",
            500,
            None,
            "The completed response could not be delivered."
        ]

    let all = policies |> List.map (fun (reason, _, _, _, _) -> reason)

    let private policy reason =
        policies |> List.find (fun (value, _, _, _, _) -> value = reason)

    let code reason =
        let _, code, _, _, _ = policy reason in code

    let status reason =
        let _, _, status, _, _ = policy reason in status

    let phase reason =
        let _, _, _, phase, _ = policy reason in phase

    let render reason =
        let _, _, _, _, message = policy reason in message

    let diagnosticId reason =
        "HOST_" + (string reason).ToUpperInvariant()

    let private diagnostic reason =
        Codec.record (fun () -> ())
        |> ResponseScalars.fixedField "id" (diagnosticId reason)
        |> Codec.field "parameters" (fun () -> ()) (Codec.record () |> Codec.build)
        |> Codec.build

    let private phaseCodec =
        Codec.enumeration
            [
                ExecutionPhase.NotStarted, "NOT_STARTED"
                ExecutionPhase.StartedUnconfirmed, "STARTED_UNCONFIRMED"
            ]
        |> Codec.nullable

    let private variant reason =
        let fields =
            Codec.record (fun _ _ _ message () _ -> { Reason = reason; Message = message })
            |> Codec.field "kind" (fun _ -> ()) (Codec.constant "HOST_FAILURE")
            |> Codec.field "code" (fun _ -> ()) (Codec.constant (code reason))
            |> Codec.field
                "status"
                (fun _ -> int64 (status reason))
                (Codec.integer (int64 (status reason)) (int64 (status reason)))
            |> Codec.field "message" _.Message ResponseScalars.nonempty
            |> Codec.field "diagnostic" (fun _ -> ()) (diagnostic reason)
            |> Codec.field
                "executionPhase"
                (fun _ -> phase reason)
                (phaseCodec
                 |> Codec.constrained
                     (match phase reason with
                      | None -> Schema.Null
                      | Some ExecutionPhase.NotStarted -> Schema.Const "NOT_STARTED"
                      | Some ExecutionPhase.StartedUnconfirmed -> Schema.Const "STARTED_UNCONFIRMED")
                     ((=) (phase reason)))
            |> Codec.build
            |> Codec.named ("HostFailure." + string reason)

        Codec.branch id (fun value -> if value.Reason = reason then Some value else None) fields

    let codec = all |> List.map variant |> Codec.choice "HostFailure"

    /// A transport status permits only the failure variants whose fixed policy uses that status.
    let responseSchema statusCode =
        let alternatives =
            all
            |> List.filter (fun reason -> status reason = statusCode)
            |> List.map (fun reason -> (variant reason).VariantSchema)

        if List.isEmpty alternatives then
            invalidArg (nameof statusCode) "No host failure uses this HTTP status."

        Schema.OneOf(alternatives, None)


    let encode reason =
        Codec.encode
            codec
            {
                Reason = reason
                Message = render reason
            }

    let decode bytes =
        Codec.decode Limits.MaximumResponseBytes codec bytes
