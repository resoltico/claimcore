namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract


module RequestCodecs =
    open Codec

    let revision = integer 0L JsonValue.MaximumInteger
    let sequence = integer 1L JsonValue.MaximumInteger

    let tokens values =
        values |> List.map (fun value -> value, value) |> enumeration

    let registration =
        (record (fun v0 v1 v2 v3 v4 v5 v6 ->
            {
                Registration.IncidentDate = v0
                IncidentNotificationDate = v1
                IncidentCountry = v2
                ClaimantName = v3
                InsurerName = v4
                ClaimedAmount = v5
                ClaimedCurrency = v6
            })
        : Fields<Registration, _>)
        |> field "incidentDate" _.IncidentDate (text)
        |> field "incidentNotificationDate" _.IncidentNotificationDate (text)
        |> field "incidentCountry" _.IncidentCountry (text)
        |> field "claimantName" _.ClaimantName (text)
        |> field "insurerName" _.InsurerName (text)
        |> field "claimedAmount" _.ClaimedAmount (text)
        |> field "claimedCurrency" _.ClaimedCurrency (text)
        |> build
        |> named "Registration"

    let decision =
        (record (fun v0 v1 v2 ->
            {
                Decision.PaymentDecisionDate = v0
                PayableAmount = v1
                PayableCurrency = v2
            })
        : Fields<Decision, _>)
        |> field "paymentDecisionDate" _.PaymentDecisionDate (text)
        |> field "payableAmount" _.PayableAmount (text)
        |> field "payableCurrency" _.PayableCurrency (text)
        |> build
        |> named "Decision"

    let payment =
        (record (fun v0 -> { Payment.PaymentDate = v0 }): Fields<Payment, _>)
        |> field "paymentDate" _.PaymentDate (text)
        |> build
        |> named "Payment"

    let registrationGroup =
        tagged
            "RegistrationGroup"
            "mode"
            [
                unitVariant
                    (CorrectionActions.token CorrectionGroupAction.Keep)
                    RegistrationGroup.Keep
                variant
                    (CorrectionActions.token CorrectionGroupAction.Replace)
                    RegistrationGroup.Replace
                    (function
                    | RegistrationGroup.Replace value -> Some value
                    | _ -> None)
                    (record id |> field "values" id registration)
            ]

    let decisionGroup =
        tagged
            "DecisionGroup"
            "mode"
            [
                unitVariant (CorrectionActions.token CorrectionGroupAction.Keep) DecisionGroup.Keep
                variant
                    (CorrectionActions.token CorrectionGroupAction.Replace)
                    DecisionGroup.Replace
                    (function
                    | DecisionGroup.Replace value -> Some value
                    | _ -> None)
                    (record id |> field "values" id decision)
                unitVariant
                    (CorrectionActions.token CorrectionGroupAction.Clear)
                    DecisionGroup.Clear
            ]

    let paymentGroup =
        tagged
            "PaymentGroup"
            "mode"
            [
                unitVariant (CorrectionActions.token CorrectionGroupAction.Keep) PaymentGroup.Keep
                variant
                    (CorrectionActions.token CorrectionGroupAction.Replace)
                    PaymentGroup.Replace
                    (function
                    | PaymentGroup.Replace value -> Some value
                    | _ -> None)
                    (record id |> field "values" id payment)
                unitVariant (CorrectionActions.token CorrectionGroupAction.Clear) PaymentGroup.Clear
            ]

    let correction =
        (record (fun v0 v1 v2 ->
            {
                Correction.Registration = v0
                Decision = v1
                Payment = v2
            })
        : Fields<Correction, _>)
        |> field "registration" _.Registration (registrationGroup)
        |> field "decision" _.Decision (decisionGroup)
        |> field "payment" _.Payment (paymentGroup)
        |> build
        |> named "Correction"

    let private businessActs =
        [
            variant
                (CaseActKinds.token (CaseActKind.Business BusinessActKind.Open))
                CaseAct.Open
                (function
                | CaseAct.Open value -> Some value
                | _ -> None)
                (record id |> field "values" id registration)
            variant
                (CaseActKinds.token (CaseActKind.Business BusinessActKind.AmendRegistration))
                CaseAct.AmendRegistration
                (function
                | CaseAct.AmendRegistration value -> Some value
                | _ -> None)
                (record id |> field "values" id registration)
            variant
                (CaseActKinds.token (CaseActKind.Business BusinessActKind.CorrectCase))
                CaseAct.CorrectCase
                (function
                | CaseAct.CorrectCase value -> Some value
                | _ -> None)
                (record id |> field "groups" id correction)
            variant
                (CaseActKinds.token (CaseActKind.Business BusinessActKind.Decide))
                CaseAct.Decide
                (function
                | CaseAct.Decide value -> Some value
                | _ -> None)
                (record id |> field "values" id decision)
            unitVariant
                (CaseActKinds.token (CaseActKind.Business BusinessActKind.WithdrawDecision))
                CaseAct.WithdrawDecision
            variant
                (CaseActKinds.token (CaseActKind.Business BusinessActKind.RecordPayment))
                CaseAct.RecordPayment
                (function
                | CaseAct.RecordPayment value -> Some value
                | _ -> None)
                (record id |> field "values" id payment)
            unitVariant
                (CaseActKinds.token (CaseActKind.Business BusinessActKind.ClearPayment))
                CaseAct.ClearPayment
            unitVariant
                (CaseActKinds.token (CaseActKind.Business BusinessActKind.Close))
                CaseAct.Close
            unitVariant
                (CaseActKinds.token (CaseActKind.Business BusinessActKind.Reopen))
                CaseAct.Reopen
        ]

    let private stewardshipActs =
        [
            variant
                (CaseActKinds.token (CaseActKind.Stewardship StewardshipActKind.Void))
                CaseAct.Void
                (function
                | CaseAct.Void value -> Some value
                | _ -> None)
                (record id |> field "reason" id text)
            variant
                (CaseActKinds.token (CaseActKind.Stewardship StewardshipActKind.Reinstate))
                CaseAct.Reinstate
                (function
                | CaseAct.Reinstate value -> Some value
                | _ -> None)
                (record id |> field "reason" id text)
            unitVariant
                (CaseActKinds.token (CaseActKind.Stewardship StewardshipActKind.Erase))
                CaseAct.Erase
        ]

    let caseAct = tagged "CaseAct" "kind" (businessActs @ stewardshipActs)

    let caseRequest =
        (record (fun v0 v1 v2 v3 ->
            {
                CaseRequest.OperationId = v0
                CaseReference = v1
                ExpectedRevision = v2
                Act = v3
            })
        : Fields<CaseRequest, _>)
        |> field "operationId" _.OperationId (uuid)
        |> field "caseReference" _.CaseReference (text)
        |> field "expectedRevision" _.ExpectedRevision (revision)
        |> field "act" _.Act (caseAct)
        |> build
        |> named "CaseRequest"

    let principal =
        (record (fun v0 v1 v2 ->
            {
                Principal.Kind = v0
                Issuer = v1
                Subject = v2
            })
        : Fields<Principal, _>)
        |> field "kind" _.Kind (tokens PrincipalKinds.all)
        |> field "issuer" _.Issuer (text)
        |> field "subject" _.Subject (text)
        |> build
        |> named "Principal"

    let scope =
        tagged
            "Scope"
            "kind"
            [
                unitVariant "INSTALLATION" GrantTarget.Installation
                variant
                    "CASE"
                    GrantTarget.CaseReference
                    (function
                    | GrantTarget.CaseReference value -> Some value
                    | _ -> None)
                    (record id |> field "caseReference" id text)
            ]

    let authorityActWith role =
        tagged
            "AuthorityAct"
            "kind"
            [
                unitVariant
                    (AuthorityKinds.token AuthorityKind.RegisterActor)
                    AuthorityAct.RegisterActor
                variant
                    (AuthorityKinds.token AuthorityKind.SetGrant)
                    AuthorityAct.SetGrant
                    (function
                    | AuthorityAct.SetGrant value -> Some value
                    | _ -> None)
                    ((record (fun role scope active ->
                        {
                            GrantChange.Role = role
                            Scope = scope
                            Active = active
                        })
                     : Fields<GrantChange, _>)
                     |> field "role" _.Role role
                     |> field "scope" _.Scope scope
                     |> field "active" _.Active boolean)
                variant
                    (AuthorityKinds.token AuthorityKind.SetEnabled)
                    AuthorityAct.SetEnabled
                    (function
                    | AuthorityAct.SetEnabled value -> Some value
                    | _ -> None)
                    (record id |> field "enabled" id boolean)
            ]

    let authorityAct =
        authorityActWith (tokens (RoleTokens.all |> List.map RoleTokens.token))

    let authorityRequest =
        (record (fun v0 v1 v2 ->
            {
                AuthorityRequest.OperationId = v0
                Target = v1
                Act = v2
            })
        : Fields<AuthorityRequest, _>)
        |> field "operationId" _.OperationId (uuid)
        |> field "target" _.Target (principal)
        |> field "act" _.Act (authorityAct)
        |> build
        |> named "AuthorityRequest"

    let caseOperation =
        caseRequest |> withScope OperationScopes.Case |> named "OperationInput.Case"

    let authorityOperation =
        authorityRequest
        |> withScope OperationScopes.Authority
        |> named "OperationInput.Authority"

    let operationInput =
        choice
            "OperationInput"
            [
                branch
                    OperationInput.Case
                    (function
                    | OperationInput.Case value -> Some value
                    | _ -> None)
                    caseOperation
                branch
                    OperationInput.Authority
                    (function
                    | OperationInput.Authority value -> Some value
                    | _ -> None)
                    authorityOperation
            ]
        |> discriminated "scope"

    let pageRequest =
        (record (fun v0 v1 -> { PageRequest.Cursor = v0; Limit = v1 }): Fields<PageRequest, _>)
        |> optionalField "cursor" _.Cursor (boundedText 1 512)
        |> field "limit" _.Limit (integer 1L (int64 Limits.MaximumPageSize))
        |> build
        |> named "PageRequest"

    let caseGetRequest =
        (record (fun v0 -> { CaseGetRequest.CaseReference = v0 }): Fields<CaseGetRequest, _>)
        |> field "caseReference" _.CaseReference (text)
        |> build
        |> named "CaseGetRequest"

    let historyRequest =
        (record (fun v0 v1 v2 v3 ->
            {
                HistoryRequest.CaseReference = v0
                Detail = v1
                Cursor = v2
                Limit = v3
            })
        : Fields<HistoryRequest, _>)
        |> field "caseReference" _.CaseReference (text)
        |> field "detail" _.Detail (tokens [ "SUMMARY"; "FULL" ])
        |> optionalField "cursor" _.Cursor (boundedText 1 512)
        |> field "limit" _.Limit (integer 1L (int64 Limits.MaximumPageSize))
        |> build
        |> named "HistoryRequest"

    let grantsRequest =
        (record (fun v0 v1 v2 ->
            {
                GrantsRequest.ActorId = v0
                Cursor = v1
                Limit = v2
            })
        : Fields<GrantsRequest, _>)
        |> field "actorId" _.ActorId (uuid)
        |> optionalField "cursor" _.Cursor (boundedText 1 512)
        |> field "limit" _.Limit (integer 1L (int64 Limits.MaximumPageSize))
        |> build
        |> named "GrantsRequest"

    let observeRequest =
        (record (fun v0 -> { ObserveRequest.OperationId = v0 }): Fields<ObserveRequest, _>)
        |> field "operationId" _.OperationId (uuid)
        |> build
        |> named "ObserveRequest"

    let empty = record () |> build |> named "Empty"
