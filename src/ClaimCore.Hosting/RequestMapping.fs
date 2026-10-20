namespace ClaimCore.Hosting

open ClaimCore.Catalog

open ClaimCore.Application
open ClaimCore.Domain

module internal RequestMapping =
    type private WireAuthorityAct = ClaimCore.Wire.AuthorityAct

    let registration (value: ClaimCore.Wire.Registration) : RegistrationInput =
        {
            IncidentDate = value.IncidentDate
            IncidentNotificationDate = value.IncidentNotificationDate
            IncidentCountry = value.IncidentCountry
            ClaimantName = value.ClaimantName
            InsurerName = value.InsurerName
            ClaimedAmount = value.ClaimedAmount
            ClaimedCurrency = value.ClaimedCurrency
        }

    let decision (value: ClaimCore.Wire.Decision) : DecisionInput =
        {
            PaymentDecisionDate = value.PaymentDecisionDate
            PayableAmount = value.PayableAmount
            PayableCurrency = value.PayableCurrency
        }

    let private correction (value: ClaimCore.Wire.Correction) : CaseCorrection =
        {
            Registration =
                match value.Registration with
                | ClaimCore.Wire.RegistrationGroup.Keep -> RegistrationCorrection.Keep
                | ClaimCore.Wire.RegistrationGroup.Replace fields ->
                    RegistrationCorrection.Replace(registration fields)
            Decision =
                match value.Decision with
                | ClaimCore.Wire.DecisionGroup.Keep -> DecisionCorrection.Keep
                | ClaimCore.Wire.DecisionGroup.Replace fields ->
                    DecisionCorrection.Replace(decision fields)
                | ClaimCore.Wire.DecisionGroup.Clear -> DecisionCorrection.Clear
            Payment =
                match value.Payment with
                | ClaimCore.Wire.PaymentGroup.Keep -> PaymentCorrection.Keep
                | ClaimCore.Wire.PaymentGroup.Replace fields ->
                    PaymentCorrection.Replace fields.PaymentDate
                | ClaimCore.Wire.PaymentGroup.Clear -> PaymentCorrection.Clear
        }

    let private business =
        function
        | ClaimCore.Wire.CaseAct.Open value -> BusinessCommand.Open(registration value)
        | ClaimCore.Wire.CaseAct.AmendRegistration value ->
            BusinessCommand.AmendRegistration(registration value)
        | ClaimCore.Wire.CaseAct.CorrectCase value -> BusinessCommand.CorrectCase(correction value)
        | ClaimCore.Wire.CaseAct.Decide value -> BusinessCommand.Decide(decision value)
        | ClaimCore.Wire.CaseAct.WithdrawDecision -> BusinessCommand.WithdrawDecision
        | ClaimCore.Wire.CaseAct.RecordPayment value ->
            BusinessCommand.RecordPayment value.PaymentDate
        | ClaimCore.Wire.CaseAct.ClearPayment -> BusinessCommand.ClearPayment
        | ClaimCore.Wire.CaseAct.Close -> BusinessCommand.Close
        | ClaimCore.Wire.CaseAct.Reopen -> BusinessCommand.Reopen
        | _ -> invalidArg "act" "Expected a business operation."

    let private act =
        function
        | ClaimCore.Wire.CaseAct.Void reason -> CaseAct.Stewardship(StewardshipAct.Void reason)
        | ClaimCore.Wire.CaseAct.Reinstate reason ->
            CaseAct.Stewardship(StewardshipAct.Reinstate reason)
        | ClaimCore.Wire.CaseAct.Erase -> CaseAct.Stewardship StewardshipAct.Erase
        | value -> CaseAct.Business(business value)

    let caseRequest (value: ClaimCore.Wire.CaseRequest) : CaseRequest =
        {
            OperationId = value.OperationId
            CaseReference = value.CaseReference
            ExpectedRevision = value.ExpectedRevision
            Act = act value.Act
        }

    let private scope =
        function
        | ClaimCore.Wire.Scope.Installation -> GrantTarget.Installation
        | ClaimCore.Wire.Scope.Case reference -> GrantTarget.CaseReference reference

    let private authorityAct =
        function
        | WireAuthorityAct.RegisterActor -> Ok AuthorityAct.RegisterActor
        | WireAuthorityAct.SetEnabled enabled -> Ok(AuthorityAct.SetEnabled enabled)
        | WireAuthorityAct.SetGrant grant ->
            match RoleTokens.tryParse grant.Role with
            | Some role -> Ok(AuthorityAct.SetGrant(role, scope grant.Scope, grant.Active))
            | None -> Error "AUTHORITY_ROLE_INVALID"

    let authorityRequest
        (value: ClaimCore.Wire.AuthorityRequest)
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
