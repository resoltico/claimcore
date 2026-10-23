namespace ClaimCore.Hosting

open ClaimCore.Contract

open ClaimCore.Application
open ClaimCore.Domain

module internal RequestMapping =
    type private WireAuthorityAct = ClaimCore.Contract.Protocol.AuthorityAct

    let registration (value: ClaimCore.Contract.Protocol.Registration) : RegistrationInput =
        {
            IncidentDate = value.IncidentDate
            IncidentNotificationDate = value.IncidentNotificationDate
            IncidentCountry = value.IncidentCountry
            ClaimantName = value.ClaimantName
            InsurerName = value.InsurerName
            ClaimedAmount = value.ClaimedAmount
            ClaimedCurrency = value.ClaimedCurrency
        }

    let decision (value: ClaimCore.Contract.Protocol.Decision) : DecisionInput =
        {
            PaymentDecisionDate = value.PaymentDecisionDate
            PayableAmount = value.PayableAmount
            PayableCurrency = value.PayableCurrency
        }

    let private correction (value: ClaimCore.Contract.Protocol.Correction) : CaseCorrection =
        {
            Registration =
                match value.Registration with
                | ClaimCore.Contract.Protocol.RegistrationGroup.Keep -> RegistrationCorrection.Keep
                | ClaimCore.Contract.Protocol.RegistrationGroup.Replace fields ->
                    RegistrationCorrection.Replace(registration fields)
            Decision =
                match value.Decision with
                | ClaimCore.Contract.Protocol.DecisionGroup.Keep -> DecisionCorrection.Keep
                | ClaimCore.Contract.Protocol.DecisionGroup.Replace fields ->
                    DecisionCorrection.Replace(decision fields)
                | ClaimCore.Contract.Protocol.DecisionGroup.Clear -> DecisionCorrection.Clear
            Payment =
                match value.Payment with
                | ClaimCore.Contract.Protocol.PaymentGroup.Keep -> PaymentCorrection.Keep
                | ClaimCore.Contract.Protocol.PaymentGroup.Replace fields ->
                    PaymentCorrection.Replace fields.PaymentDate
                | ClaimCore.Contract.Protocol.PaymentGroup.Clear -> PaymentCorrection.Clear
        }

    let private business =
        function
        | ClaimCore.Contract.Protocol.CaseAct.Open value -> BusinessCommand.Open(registration value)
        | ClaimCore.Contract.Protocol.CaseAct.AmendRegistration value ->
            BusinessCommand.AmendRegistration(registration value)
        | ClaimCore.Contract.Protocol.CaseAct.CorrectCase value ->
            BusinessCommand.CorrectCase(correction value)
        | ClaimCore.Contract.Protocol.CaseAct.Decide value -> BusinessCommand.Decide(decision value)
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
