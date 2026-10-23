namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract

type HostFailureReply =
    { Reason: HostFailure; Message: string }

module HostFailures =
    let private policies =
        [
            HostFailure.ContractMismatch,
            409,
            Some ExecutionPhase.NotStarted,
            (MessageText.literal "diagnostic.HOST_CONTRACTMISMATCH")
            HostFailure.ConnectionRejected,
            403,
            Some ExecutionPhase.NotStarted,
            (MessageText.literal "diagnostic.HOST_CONNECTIONREJECTED")
            HostFailure.OriginRejected,
            403,
            Some ExecutionPhase.NotStarted,
            (MessageText.literal "diagnostic.HOST_ORIGINREJECTED")
            HostFailure.MediaTypeRejected,
            415,
            Some ExecutionPhase.NotStarted,
            (MessageText.literal "diagnostic.HOST_MEDIATYPEREJECTED")
            HostFailure.BodyTooLarge,
            413,
            Some ExecutionPhase.NotStarted,
            (MessageText.literal "diagnostic.HOST_BODYTOOLARGE")
            HostFailure.SessionRejected,
            401,
            Some ExecutionPhase.NotStarted,
            (MessageText.literal "diagnostic.HOST_SESSIONREJECTED")
            HostFailure.SessionForbidden,
            403,
            Some ExecutionPhase.NotStarted,
            (MessageText.literal "diagnostic.HOST_SESSIONFORBIDDEN")
            HostFailure.MethodRejected,
            405,
            Some ExecutionPhase.NotStarted,
            (MessageText.literal "diagnostic.HOST_METHODREJECTED")
            HostFailure.AntiforgeryRejected,
            403,
            Some ExecutionPhase.NotStarted,
            (MessageText.literal "diagnostic.HOST_ANTIFORGERYREJECTED")
            HostFailure.EndpointMissing,
            404,
            Some ExecutionPhase.NotStarted,
            (MessageText.literal "diagnostic.HOST_ENDPOINTMISSING")
            HostFailure.RateLimited,
            429,
            Some ExecutionPhase.NotStarted,
            (MessageText.literal "diagnostic.HOST_RATELIMITED")
            HostFailure.InputInvalid,
            400,
            Some ExecutionPhase.NotStarted,
            (MessageText.literal "diagnostic.HOST_INPUTINVALID")
            HostFailure.BeforeDispatchFailed,
            500,
            Some ExecutionPhase.NotStarted,
            (MessageText.literal "diagnostic.HOST_BEFOREDISPATCHFAILED")
            HostFailure.DispatchUnconfirmed,
            500,
            Some ExecutionPhase.StartedUnconfirmed,
            (MessageText.literal "diagnostic.HOST_DISPATCHUNCONFIRMED")
            HostFailure.CompletedResponseFailed,
            500,
            None,
            (MessageText.literal "diagnostic.HOST_COMPLETEDRESPONSEFAILED")
        ]

    let private principalFailures =
        [
            for target, _ in InputDiagnostics.targets do
                for issue, _ in InputDiagnostics.issues do
                    HostFailure.PrincipalInput { Target = target; Issue = issue }
        ]

    let all =
        (policies |> List.map (fun (reason, _, _, _) -> reason)) @ principalFailures

    let private policy reason =
        let reason =
            match reason with
            | HostFailure.PrincipalInput _ -> HostFailure.InputInvalid
            | value -> value

        policies |> List.find (fun (value, _, _, _) -> value = reason)

    let code = HostFailureIds.token

    let status reason =
        let _, status, _, _ = policy reason in status

    let phase reason =
        let _, _, phase, _ = policy reason in phase

    let render reason =
        let _, _, _, message = policy reason in message

    let diagnosticId =
        function
        | HostFailure.PrincipalInput _ -> InputDiagnostics.principalId
        | value -> HostFailureIds.diagnostic value

    let private diagnostic reason =
        match reason with
        | HostFailure.PrincipalInput issue ->
            let parameters =
                Codec.record ()
                |> ResponseScalars.fixedField
                    "target"
                    (InputDiagnostics.targets |> List.find (fst >> (=) issue.Target) |> snd)
                |> ResponseScalars.fixedField
                    "issue"
                    (InputDiagnostics.issues |> List.find (fst >> (=) issue.Issue) |> snd)
                |> Codec.build

            Codec.record (fun () -> ())
            |> ResponseScalars.fixedField "id" (diagnosticId reason)
            |> Codec.field "parameters" id parameters
            |> Codec.build
        | _ ->
            Codec.record (fun () -> ())
            |> ResponseScalars.fixedField "id" (diagnosticId reason)
            |> Codec.field "parameters" (fun () -> ()) (Codec.record () |> Codec.build)
            |> Codec.build

    let private phaseCodec = Codec.enumeration DeliveryTokens.phases |> Codec.nullable

    let private name =
        function
        | HostFailure.PrincipalInput issue ->
            "PrincipalInput." + string issue.Target + "." + string issue.Issue
        | reason -> string reason

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
                      | Some ExecutionPhase.NotStarted ->
                          Schema.Const(DeliveryTokens.phase ExecutionPhase.NotStarted)
                      | Some ExecutionPhase.StartedUnconfirmed ->
                          Schema.Const(DeliveryTokens.phase ExecutionPhase.StartedUnconfirmed))
                     ((=) (phase reason)))
            |> Codec.build
            |> Codec.named ("HostFailure." + name reason)

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
