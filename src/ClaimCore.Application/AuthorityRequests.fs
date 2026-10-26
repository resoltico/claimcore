namespace ClaimCore.Application

open ClaimCore.Contract

open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Shared conversion between the primitive record codec and admitted application identities.
module AuthorityRequests =
    type private SharedAuthorityAct = ClaimCore.Domain.AuthorityAct

    /// Only a case-scope reference has alternate identity spellings; principals stay exact.
    let canonical (request: AuthorityRequestRecord) =
        let grant (value: GrantChange) =
            let scope =
                match value.Scope with
                | GrantTarget.Installation -> GrantTarget.Installation
                | GrantTarget.CaseReference reference ->
                    GrantTarget.CaseReference(
                        if System.String.IsNullOrEmpty reference then
                            reference
                        else
                            CaseReference.key reference
                    )

            { value with Scope = scope }

        let act =
            match request.Act with
            | RecordedAuthorityAct.Operation(SharedAuthorityAct.Grant value) ->
                RecordedAuthorityAct.Operation(SharedAuthorityAct.Grant(grant value))
            | RecordedAuthorityAct.Operation(SharedAuthorityAct.Revoke value) ->
                RecordedAuthorityAct.Operation(SharedAuthorityAct.Revoke(grant value))
            | act -> act

        { request with Act = act }

    let private recordAct =
        let grant role target =
            {
                Role = RoleTokens.token role
                Scope = target
            }

        function
        | AdmittedAuthorityAct.Register -> SharedAuthorityAct.Register
        | AdmittedAuthorityAct.Grant(role, target) -> SharedAuthorityAct.Grant(grant role target)
        | AdmittedAuthorityAct.Revoke(role, target) -> SharedAuthorityAct.Revoke(grant role target)
        | AdmittedAuthorityAct.Enable -> SharedAuthorityAct.Enable
        | AdmittedAuthorityAct.Disable -> SharedAuthorityAct.Disable

    let toRecord (request: AdmittedAuthorityRequest) : AuthorityRequestRecord =
        {
            OperationId = request.OperationId
            Target = PrincipalKey.describe request.Target
            Act = RecordedAuthorityAct.Operation(recordAct request.Act)
        }

    let private admittedAct =
        let grant make (value: GrantChange) =
            match RoleTokens.parse value.Role with
            | Some role -> Ok(make (role, value.Scope))
            | None -> Error AuthorityInputFailure.RoleInvalid

        function
        | RecordedAuthorityAct.Operation SharedAuthorityAct.Register ->
            Ok AdmittedAuthorityAct.Register
        | RecordedAuthorityAct.Operation(SharedAuthorityAct.Grant value) ->
            grant AdmittedAuthorityAct.Grant value
        | RecordedAuthorityAct.Operation(SharedAuthorityAct.Revoke value) ->
            grant AdmittedAuthorityAct.Revoke value
        | RecordedAuthorityAct.Operation SharedAuthorityAct.Enable -> Ok AdmittedAuthorityAct.Enable
        | RecordedAuthorityAct.Operation SharedAuthorityAct.Disable ->
            Ok AdmittedAuthorityAct.Disable
        | RecordedAuthorityAct.ProvisionOwner -> Error AuthorityInputFailure.ActInvalid

    let fromRecord
        (request: AuthorityRequestRecord)
        : Result<AdmittedAuthorityRequest, AuthorityInputFailure> =
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
        | Error _, _ -> Error AuthorityInputFailure.PrincipalInvalid
        | _, Error error -> Error error

    let internal provisionRecord operationId target : AuthorityRequestRecord =
        {
            OperationId = operationId
            Target = PrincipalKey.describe target
            Act = RecordedAuthorityAct.ProvisionOwner
        }

    let toInput (request: AdmittedAuthorityRequest) : ClaimCore.Domain.AuthorityRequest =
        {
            OperationId = request.OperationId
            Target = PrincipalKey.describe request.Target
            Act = recordAct request.Act
        }

    let admit
        (request: ClaimCore.Domain.AuthorityRequest)
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
        | AdmittedAuthorityAct.Grant(_, GrantTarget.CaseReference reference)
        | AdmittedAuthorityAct.Revoke(_, GrantTarget.CaseReference reference) -> Some reference
        | _ -> None

    let internal validate (request: AdmittedAuthorityRequest) =
        if request.OperationId = System.Guid.Empty then
            Error((Refusal.InvalidInput(InputTarget.OperationId, Violation.OperationIdRequired)))
        else
            targetCase request
            |> Option.map (
                (CaseReference.parse
                 >> Result.map ignore
                 >> Result.mapError (fun violation ->
                     Refusal.InvalidInput(InputTarget.Field BusinessField.CaseReference, violation)))
                >> Result.mapError (fun value -> value)
            )
            |> Option.defaultValue (Ok())
