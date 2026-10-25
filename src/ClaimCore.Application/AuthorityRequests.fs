namespace ClaimCore.Application

open ClaimCore.Contract

open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Shared conversion between the primitive record codec and admitted application identities.
module AuthorityRequests =
    type private SharedAuthorityAct = ClaimCore.Contract.AuthorityAct

    let toRecord (request: AdmittedAuthorityRequest) : AuthorityRequestRecord =
        let act =
            match request.Act with
            | AdmittedAuthorityAct.RegisterActor ->
                RecordedAuthorityAct.Operation(SharedAuthorityAct.RegisterActor)
            | AdmittedAuthorityAct.SetEnabled value ->
                RecordedAuthorityAct.Operation(SharedAuthorityAct.SetEnabled value)
            | AdmittedAuthorityAct.SetGrant(role, target, active) ->
                RecordedAuthorityAct.Operation(
                    SharedAuthorityAct.SetGrant
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
        | RecordedAuthorityAct.Operation(SharedAuthorityAct.RegisterActor) ->
            Ok AdmittedAuthorityAct.RegisterActor
        | RecordedAuthorityAct.Operation(SharedAuthorityAct.SetEnabled value) ->
            Ok(AdmittedAuthorityAct.SetEnabled value)
        | RecordedAuthorityAct.Operation(SharedAuthorityAct.SetGrant {
                                                                         Role = role
                                                                         Scope = scope
                                                                         Active = active
                                                                     }) ->
            match RoleTokens.parse role with
            | Some role -> Ok(AdmittedAuthorityAct.SetGrant(role, scope, active))
            | None -> Error "AUTHORITY_ROLE_INVALID"
        | RecordedAuthorityAct.ProvisionOwner -> Error "AUTHORITY_ACT_INVALID"

    let fromRecord (request: AuthorityRequestRecord) : Result<AdmittedAuthorityRequest, string> =
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

    let toInput (request: AdmittedAuthorityRequest) : ClaimCore.Contract.AuthorityRequest =
        let record = toRecord request

        {
            OperationId = record.OperationId
            Target = record.Target
            Act =
                match record.Act with
                | RecordedAuthorityAct.Operation act -> act
                | RecordedAuthorityAct.ProvisionOwner ->
                    invalidOp "Owner provisioning is administrative."
        }

    let admit
        (request: ClaimCore.Contract.AuthorityRequest)
        : Result<AdmittedAuthorityRequest, Refusal> =
        match
            PrincipalKey.admit request.Target.Kind request.Target.Issuer request.Target.Subject,
            admittedAct (RecordedAuthorityAct.Operation request.Act)
        with
        | Error reason, _ -> Error reason
        | _, Error _ -> Error Refusal.ActNotPermitted
        | Ok target, Ok act ->
            Ok
                {
                    OperationId = request.OperationId
                    Target = target
                    Act = act
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

    let internal targetCase (request: AdmittedAuthorityRequest) =
        match request.Act with
        | AdmittedAuthorityAct.SetGrant(_, GrantTarget.CaseReference reference, _) -> Some reference
        | _ -> None

    let internal validate (request: AdmittedAuthorityRequest) =
        if request.OperationId = System.Guid.Empty then
            Error((Refusal.InvalidInput(InputTarget.OperationId, Violation.OperationIdRequired)))
        else
            targetCase request
            |> Option.map (CaseReference.validate >> Result.mapError (fun value -> value))
            |> Option.defaultValue (Ok())
