namespace ClaimCore.Hosting

open ClaimCore.Contract

open ClaimCore.Application
open ClaimCore.Domain

module internal RequestMapping =
    type private WireAuthorityAct = ClaimCore.Contract.Protocol.AuthorityAct

    let private business =
        function
        | ClaimCore.Contract.Protocol.CaseAct.Open value -> BusinessCommand.Open value
        | ClaimCore.Contract.Protocol.CaseAct.AmendRegistration value ->
            BusinessCommand.AmendRegistration value
        | ClaimCore.Contract.Protocol.CaseAct.CorrectCase value -> BusinessCommand.CorrectCase value
        | ClaimCore.Contract.Protocol.CaseAct.Decide value -> BusinessCommand.Decide value
        | ClaimCore.Contract.Protocol.CaseAct.WithdrawDecision -> BusinessCommand.WithdrawDecision
        | ClaimCore.Contract.Protocol.CaseAct.RecordPayment value ->
            BusinessCommand.RecordPayment value.PaymentDate
        | ClaimCore.Contract.Protocol.CaseAct.ClearPayment -> BusinessCommand.ClearPayment
        | ClaimCore.Contract.Protocol.CaseAct.Close -> BusinessCommand.Close
        | ClaimCore.Contract.Protocol.CaseAct.Reopen -> BusinessCommand.Reopen
        | _ -> invalidArg "act" "Expected a business operation."

    let private act =
        function
        | ClaimCore.Contract.Protocol.CaseAct.Void reason ->
            CaseAct.Stewardship(StewardshipAct.Void reason)
        | ClaimCore.Contract.Protocol.CaseAct.Reinstate reason ->
            CaseAct.Stewardship(StewardshipAct.Reinstate reason)
        | ClaimCore.Contract.Protocol.CaseAct.Erase -> CaseAct.Stewardship StewardshipAct.Erase
        | value -> CaseAct.Business(business value)

    let caseRequest (value: ClaimCore.Contract.Protocol.CaseRequest) : CaseRequest =
        {
            OperationId = value.OperationId
            CaseReference = value.CaseReference
            ExpectedRevision = value.ExpectedRevision
            Act = act value.Act
        }

    let private scope =
        function
        | ClaimCore.Contract.Protocol.Scope.Installation -> GrantTarget.Installation
        | ClaimCore.Contract.Protocol.Scope.Case reference -> GrantTarget.CaseReference reference

    let private authorityAct =
        function
        | WireAuthorityAct.RegisterActor -> Ok AuthorityAct.RegisterActor
        | WireAuthorityAct.SetEnabled enabled -> Ok(AuthorityAct.SetEnabled enabled)
        | WireAuthorityAct.SetGrant grant ->
            match RoleTokens.tryParse grant.Role with
            | Some role -> Ok(AuthorityAct.SetGrant(role, scope grant.Scope, grant.Active))
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
