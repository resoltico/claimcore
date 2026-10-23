namespace ClaimCore.Application

open ClaimCore.Contract

open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Shared conversion between the primitive record codec and admitted application identities.
module AuthorityRequests =
    type private WireAuthorityAct = ClaimCore.Contract.Protocol.AuthorityAct

    let toRecord (request: AuthorityRequest) : AuthorityRequestRecord =
        let act =
            match request.Act with
            | AuthorityAct.RegisterActor ->
                RecordedAuthorityAct.Operation(WireAuthorityAct.RegisterActor)
            | AuthorityAct.SetEnabled value ->
                RecordedAuthorityAct.Operation(WireAuthorityAct.SetEnabled value)
            | AuthorityAct.SetGrant(role, target, active) ->
                RecordedAuthorityAct.Operation(
                    WireAuthorityAct.SetGrant
                        {
                            Role = RoleTokens.token role
                            Scope = target
                            Active = active
                        }
                )

        {
            OperationId = request.OperationId
            Target = PrincipalKey.describe request.Target
            Act = act
        }

    let private admittedAct =
        function
        | RecordedAuthorityAct.Operation(WireAuthorityAct.RegisterActor) ->
            Ok AuthorityAct.RegisterActor
        | RecordedAuthorityAct.Operation(WireAuthorityAct.SetEnabled value) ->
            Ok(AuthorityAct.SetEnabled value)
        | RecordedAuthorityAct.Operation(WireAuthorityAct.SetGrant {
                                                                       Role = role
                                                                       Scope = scope
                                                                       Active = active
                                                                   }) ->
            match RoleTokens.tryParse role with
            | Some role -> Ok(AuthorityAct.SetGrant(role, scope, active))
            | None -> Error "AUTHORITY_ROLE_INVALID"
        | RecordedAuthorityAct.ProvisionOwner -> Error "AUTHORITY_ACT_INVALID"

    let fromRecord (request: AuthorityRequestRecord) : Result<AuthorityRequest, string> =
        match
            PrincipalKey.fromStorage
                request.Target.Kind
                request.Target.Issuer
                request.Target.Subject,
            admittedAct request.Act
        with
        | Ok target, Ok act ->
            Ok
                {
                    OperationId = request.OperationId
                    Target = target
                    Act = act
                }
        | Error _, _ -> Error "ACTOR_PRINCIPAL_INVALID"
        | _, Error error -> Error error

    let internal provisionRecord operationId target : AuthorityRequestRecord =
        {
            OperationId = operationId
            Target = PrincipalKey.describe target
            Act = RecordedAuthorityAct.ProvisionOwner
        }

    /// Permanent record interpretation does not replay today's grants or capability policy.
    let internal validRecord (request: AuthorityRequestRecord) =
        let principal =
            PrincipalKey.fromStorage
                request.Target.Kind
                request.Target.Issuer
                request.Target.Subject

        match request.Act with
        | RecordedAuthorityAct.ProvisionOwner -> principal |> Result.exists PrincipalKey.isHuman
        | _ -> fromRecord request |> Result.isOk

    let internal targetCase (request: AuthorityRequest) =
        match request.Act with
        | AuthorityAct.SetGrant(_, GrantTarget.CaseReference reference, _) -> Some reference
        | _ -> None

    let internal validate (request: AuthorityRequest) =
        if request.OperationId = System.Guid.Empty then
            Error(
                Rejection.Case(
                    CaseRefusal.InvalidInput(
                        InputTarget.OperationId,
                        InputViolation.Act ActViolation.EmptyOperationId
                    )
                )
            )
        else
            targetCase request
            |> Option.map (CaseReference.validate >> Result.mapError Rejection.Case)
            |> Option.defaultValue (Ok())
