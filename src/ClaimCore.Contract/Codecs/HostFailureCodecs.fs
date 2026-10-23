namespace ClaimCore.Contract

open ClaimCore.Contract.Protocol

module HostFailureCodecs =
    let failure =
        (Codec.record (fun code phase -> { Code = code; Phase = phase }): Fields<HostFailure, _>)
        |> Codec.field
            "code"
            _.Code
            (RequestCodecs.tokens (HostFailures.all |> List.map HostFailures.code |> List.distinct))
        |> Codec.field "phase" _.Phase (Codec.nullable (Codec.enumeration DeliveryTokens.phases))
        |> Codec.build
        |> Codec.named "HostFailure"

    let reply = Codec.record id |> Codec.field "hostFailure" id failure |> Codec.build

    let ofReason reason =
        {
            Code = HostFailures.code reason
            Phase = HostFailures.phase reason
        }

    let encode reason = ofReason reason |> Codec.encode reply

    let decode bytes =
        Codec.decode Limits.MaximumResponseBytes reply bytes
