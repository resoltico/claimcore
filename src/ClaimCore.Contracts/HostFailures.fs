namespace ClaimCore.Contracts

open System.Text
open WireSchema
open WireJson

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

module HostFailures =
    let private policies =
        [
            HostFailure.ContractMismatch,
            "CONTRACT_MISMATCH",
            409,
            Some "NOT_STARTED",
            "The client contract differs. Reload or use a matching client."
            HostFailure.ConnectionRejected,
            "CONNECTION_REJECTED",
            403,
            Some "NOT_STARTED",
            "Connection was refused."
            HostFailure.OriginRejected,
            "ORIGIN_REJECTED",
            403,
            Some "NOT_STARTED",
            "Origin was refused."
            HostFailure.MediaTypeRejected,
            "MEDIA_TYPE_REJECTED",
            415,
            Some "NOT_STARTED",
            "Use the endpoint media type."
            HostFailure.BodyTooLarge,
            "BODY_TOO_LARGE",
            413,
            Some "NOT_STARTED",
            "Request body is too large."
            HostFailure.SessionRejected,
            "SESSION_REJECTED",
            401,
            Some "NOT_STARTED",
            "Authentication was refused."
            HostFailure.SessionForbidden,
            "SESSION_FORBIDDEN",
            403,
            Some "NOT_STARTED",
            "Session access was refused."
            HostFailure.MethodRejected,
            "METHOD_REJECTED",
            405,
            Some "NOT_STARTED",
            "The method is unsupported."
            HostFailure.AntiforgeryRejected,
            "ANTIFORGERY_REJECTED",
            403,
            Some "NOT_STARTED",
            "Request verification was refused."
            HostFailure.EndpointMissing,
            "ENDPOINT_MISSING",
            404,
            Some "NOT_STARTED",
            "Endpoint was not found."
            HostFailure.RateLimited,
            "RATE_LIMITED",
            429,
            Some "NOT_STARTED",
            "Request admission is busy."
            HostFailure.InputInvalid,
            "INPUT_INVALID",
            400,
            Some "NOT_STARTED",
            "The input does not satisfy the endpoint contract."
            HostFailure.BeforeDispatchFailed,
            "INTERNAL",
            500,
            Some "NOT_STARTED",
            "Processing failed before dispatch."
            HostFailure.DispatchUnconfirmed,
            "INTERNAL",
            500,
            Some "STARTED_UNCONFIRMED",
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

    let schema =
        policies
        |> List.map (fun (reason, code, status, phase, _) ->
            objectOf
                [
                    property "kind" (token "HOST_FAILURE")
                    property "code" (token code)
                    property "status" (Schema.constant (IntegerConstant(int64 status)))
                    property "message" WireSchema.text
                    property
                        "diagnostic"
                        (objectOf
                            [
                                property "id" (token (diagnosticId reason))
                                property "parameters" (objectOf [])
                            ])
                    property
                        "executionPhase"
                        (phase |> Option.map token |> Option.defaultValue Schema.nullValue)
                ])
        |> Schema.oneOf
        |> fun root -> document "host-failure.schema.json" root []

    let encode reason =
        let _, code, status, phase, message = policy reason

        obj
            [
                "kind", text "HOST_FAILURE"
                "code", text code
                "status", number status
                "message", text message
                "diagnostic", obj [ "id", text (diagnosticId reason); "parameters", obj [] ]
                "executionPhase", optional text phase
            ]
        |> fun value -> value.ToJsonString() |> Encoding.UTF8.GetBytes

    let decode bytes =
        HttpInputSupport.parse bytes (fun root ->
            if not (SchemaValueValidation.verify schema root) then
                HttpInputSupport.fail HttpInputProblem.InvalidJson

            let id =
                root.GetProperty("diagnostic").GetProperty("id").GetString() |> Option.ofObj

            all |> List.find (fun reason -> Some(diagnosticId reason) = id))
