namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract

open ClaimCore.Json


module HostFailureCodecs =
    let ofReason reason =
        {
            Code = HostFailures.code reason
            Phase = HostFailures.phase reason
        }

    let private shape reason =
        Schema.Object(
            [
                "code", Schema.Const(HostFailures.code reason)
                "phase",
                (match HostFailures.phase reason with
                 | None -> Schema.Null
                 | Some phase -> Schema.Const(DeliveryTokens.phase phase))
            ],
            []
        )

    let private allowed failure =
        HostFailures.all |> List.exists (fun reason -> ofReason reason = failure)

    let matchesStatus status failure =
        HostFailures.all
        |> List.exists (fun reason ->
            HostFailures.status reason = status && ofReason reason = failure)

    let failure =
        (Codec.record (fun code phase -> { Code = code; Phase = phase }): Fields<HostFailure, _>)
        |> Codec.field
            "code"
            _.Code
            (RequestCodecs.tokens (HostFailures.all |> List.map HostFailures.code |> List.distinct))
        |> Codec.field "phase" _.Phase (Codec.nullable (Codec.enumeration DeliveryTokens.phases))
        |> Codec.build
        |> Codec.constrained (Schema.OneOf(HostFailures.all |> List.map shape, None)) allowed
        |> Codec.named "HostFailure"

    let reply = Codec.record id |> Codec.field "hostFailure" id failure |> Codec.build

    let responseSchema status =
        let alternatives =
            HostFailures.all
            |> List.filter (fun reason -> HostFailures.status reason = status)
            |> List.map shape

        if List.isEmpty alternatives then
            invalidArg (nameof status) "No host failure uses this HTTP status."

        Schema.Object([ "hostFailure", Schema.OneOf(alternatives, None) ], [])

    let encode reason = ofReason reason |> Codec.encode reply

    let decode bytes =
        Codec.decode Limits.MaximumResponseBytes reply bytes
