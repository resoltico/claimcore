namespace ClaimCore.Hosting

open ClaimCore.Contract

open ClaimCore.Application
open ClaimCore.Domain

module internal RequestMapping =
    type private WireAuthorityAct = ClaimCore.Contract.Protocol.AuthorityAct

    let private authorityAct =
        function
        | WireAuthorityAct.RegisterActor -> Ok AuthorityAct.RegisterActor
        | WireAuthorityAct.SetEnabled enabled -> Ok(AuthorityAct.SetEnabled enabled)
        | WireAuthorityAct.SetGrant grant ->
            match RoleTokens.tryParse grant.Role with
            | Some role -> Ok(AuthorityAct.SetGrant(role, grant.Scope, grant.Active))
            | None -> Error "AUTHORITY_ROLE_INVALID"

    let authorityRequest
        (value: ClaimCore.Contract.Protocol.AuthorityRequest)
        : Result<AuthorityRequest, HostFailure> =
        let principal =
            PrincipalKey.admit value.Target.Kind value.Target.Issuer value.Target.Subject
            |> Result.mapError HostFailure.PrincipalInput

        match
            principal, authorityAct value.Act |> Result.mapError (fun _ -> HostFailure.InputInvalid)
        with
        | Ok target, Ok operation ->
            Ok
                {
                    OperationId = value.OperationId
                    Target = target
                    Act = operation
                }
        | Error reason, _
        | _, Error reason -> Error reason
