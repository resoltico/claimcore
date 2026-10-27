namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Protocol

open ClaimCore.Json


module HostFailureCodecs =
    let ofReason reason =
        {
            Code = HostFailureReason.code reason
            Phase = HostFailureReason.phase reason
        }

    let private shape reason =
        Schema.Object(
            [
                "code", Schema.Const(HostFailureReason.code reason)
                "phase",
                (match HostFailureReason.phase reason with
                 | None -> Schema.Null
                 | Some phase -> Schema.Const(ExecutionPhase.phase phase))
            ],
            []
        )

    let private allowed failure =
        HostFailureReason.all |> List.exists (fun reason -> ofReason reason = failure)

    let matchesStatus status failure =
        HostFailureReason.all
        |> List.exists (fun reason ->
            HostFailureReason.status reason = status && ofReason reason = failure)

    let failure =
        (Codec.record (fun code phase -> { Code = code; Phase = phase }): Fields<HostFailure, _>)
        |> Codec.field
            "code"
            _.Code
            (RequestCodecs.tokens (
                HostFailureReason.all |> List.map HostFailureReason.code |> List.distinct
            ))
        |> Codec.field "phase" _.Phase (Codec.nullable (Codec.enumeration ExecutionPhase.phases))
        |> Codec.build
        |> Codec.constrained (Schema.OneOf(HostFailureReason.all |> List.map shape, None)) allowed
        |> Codec.named "HostFailure"

    let reply = Codec.record id |> Codec.field "hostFailure" id failure |> Codec.build

    let responseSchema status =
        let alternatives =
            HostFailureReason.all
            |> List.filter (fun reason -> HostFailureReason.status reason = status)
            |> List.map shape

        if List.isEmpty alternatives then
            invalidArg (nameof status) "No host failure uses this HTTP status."

        Schema.Object([ "hostFailure", Schema.OneOf(alternatives, None) ], [])

    let encode reason = ofReason reason |> Codec.encode reply

    let decode bytes =
        Codec.decode Limits.MaximumResponseBytes reply bytes
