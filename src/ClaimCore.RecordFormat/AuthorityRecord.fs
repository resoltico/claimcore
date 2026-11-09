namespace ClaimCore.RecordFormat

open System
open ClaimCore.Json
open ClaimCore.Domain

module AuthorityRecord =
    open Codec

    let private actorId =
        uuid
        |> interpret
            (fun path value ->
                ActorId.create value
                |> Option.map Ok
                |> Option.defaultValue (
                    Error
                        {
                            Path = path
                            Reason = "Invalid target actor."
                        }
                ))
            ActorId.value

    let private profile =
        CanonicalInputs.actorProfileInput
        |> interpret
            (fun path input ->
                ActorProfile.restore input
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid retained profile."
                    }))
            ActorProfile.authored

    let private act =
        let principal = record id |> field "principal" id RecordCodecs.principal
        let profile = record id |> field "profile" id profile

        let grant =
            record (fun role scope ->
                {
                    RecordedGrant.Role = role
                    Scope = scope
                })
            |> field "role" _.Role RecordCodecs.role
            |> field "scope" _.Scope RecordCodecs.scope

        taggedOn
            "kind"
            "RecordedAuthorityEffect"
            [
                variant
                    "REGISTER"
                    AuthorityEffect.Register
                    (function
                    | AuthorityEffect.Register value -> Some value
                    | _ -> None)
                    principal
                variant
                    "PROVISION_OWNER"
                    AuthorityEffect.ProvisionOwner
                    (function
                    | AuthorityEffect.ProvisionOwner value -> Some value
                    | _ -> None)
                    principal
                variant
                    "SET_PROFILE"
                    AuthorityEffect.SetProfile
                    (function
                    | AuthorityEffect.SetProfile value -> Some value
                    | _ -> None)
                    profile
                unitVariant "ERASE_PROFILE" AuthorityEffect.EraseProfile
                variant
                    "GRANT"
                    AuthorityEffect.Grant
                    (function
                    | AuthorityEffect.Grant value -> Some value
                    | _ -> None)
                    grant
                variant
                    "REVOKE"
                    AuthorityEffect.Revoke
                    (function
                    | AuthorityEffect.Revoke value -> Some value
                    | _ -> None)
                    grant
                unitVariant "ENABLE" AuthorityEffect.Enable
                unitVariant "DISABLE" AuthorityEffect.Disable
            ]

    let private codec =
        let fields =
            (record (fun operation target effect ->
                {
                    AuthorityEffectRecord.OperationId = operation
                    TargetActorId = target
                    Effect = effect
                })
            : Fields<AuthorityEffectRecord, _>)
            |> field "operationId" _.OperationId uuid
            |> field "targetActorId" _.TargetActorId actorId
            |> field "act" _.Effect act

        taggedOn "kind" "AuthorityEffectRecord.Subject" [ variant "AUTHORITY" id Some fields ]
        |> named "AuthorityEffectRecord"

    let private validate (record: AuthorityEffectRecord) =
        let principal =
            match record.Effect with
            | AuthorityEffect.Register value
            | AuthorityEffect.ProvisionOwner value -> Some value
            | _ -> None

        if
            record.OperationId = Guid.Empty
            || (match record.Effect with
                | AuthorityEffect.ProvisionOwner input -> input.Kind <> PrincipalKind.Human
                | _ -> false)
            || (principal
                |> Option.exists (fun value ->
                    PrincipalKey.ofKind value.Kind value.Issuer value.Subject |> Result.isError))
        then
            Error
                {
                    Field = "$"
                    Message = "Invalid authority effect values."
                }
        else
            Ok record

    let decode maximumBytes bytes =
        Codec.decode (min maximumBytes RecordLimits.MaximumRequestRecordBytes) codec bytes
        |> Result.mapError (fun problem ->
            {
                Field = problem.Path
                Message = problem.Reason
            })
        |> Result.bind validate

    let encode record =
        match validate record with
        | Ok record -> codec.Write record |> CanonicalWriter.bytes |> RecordLimits.requestBytes
        | Error _ -> invalidArg (nameof record) "Invalid authority effect values."
