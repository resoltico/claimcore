namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Json

/// Actor references intentionally exclude contact and principal identity details.
module internal ActorCodecs =
    open Codec
    open ResponseScalars

    let displayName =
        tagged
            "DisplayName"
            [
                variant
                    "NAMED"
                    DisplayName.Named
                    (function
                    | DisplayName.Named value -> Some value
                    | DisplayName.Erased -> None)
                    (record id |> field "value" id partyName)
                unitVariant "ERASED" DisplayName.Erased
            ]

    let actorRef =
        (record (fun actor name kind availability ->
            {
                ActorRef.ActorId = actor
                DisplayName = name
                PrincipalKind = kind
                Availability = availability
            })
        : Fields<ActorRef, _>)
        |> field "actorId" _.ActorId actorId
        |> field "displayName" _.DisplayName displayName
        |> field "principalKind" _.PrincipalKind (VocabularyCodec.create PrincipalKind.vocabulary)
        |> field "availability" _.Availability (VocabularyCodec.create ActorAvailability.vocabulary)
        |> build
        |> named "ActorRef"
