namespace ClaimCore.Wire

module RequestCodecs =
    open Codec

    let revision = integer 0L Strict.MaximumSafeInteger
    let sequence = integer 1L Strict.MaximumSafeInteger

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
                unitVariant "KEEP" RegistrationGroup.Keep
                variant
                    "REPLACE"
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
                unitVariant "KEEP" DecisionGroup.Keep
                variant
                    "REPLACE"
                    DecisionGroup.Replace
                    (function
                    | DecisionGroup.Replace value -> Some value
                    | _ -> None)
                    (record id |> field "values" id decision)
                unitVariant "CLEAR" DecisionGroup.Clear
            ]

    let paymentGroup =
        tagged
            "PaymentGroup"
            "mode"
            [
                unitVariant "KEEP" PaymentGroup.Keep
                variant
                    "REPLACE"
                    PaymentGroup.Replace
                    (function
                    | PaymentGroup.Replace value -> Some value
                    | _ -> None)
                    (record id |> field "values" id payment)
                unitVariant "CLEAR" PaymentGroup.Clear
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
                "OPEN"
                CaseAct.Open
                (function
                | CaseAct.Open value -> Some value
                | _ -> None)
                (record id |> field "values" id registration)
            variant
                "AMEND_REGISTRATION"
                CaseAct.AmendRegistration
                (function
                | CaseAct.AmendRegistration value -> Some value
                | _ -> None)
                (record id |> field "values" id registration)
            variant
                "CORRECT_CASE"
                CaseAct.CorrectCase
                (function
                | CaseAct.CorrectCase value -> Some value
                | _ -> None)
                (record id |> field "groups" id correction)
            variant
                "DECIDE"
                CaseAct.Decide
                (function
                | CaseAct.Decide value -> Some value
                | _ -> None)
                (record id |> field "values" id decision)
            unitVariant "WITHDRAW_DECISION" CaseAct.WithdrawDecision
            variant
                "RECORD_PAYMENT"
                CaseAct.RecordPayment
                (function
                | CaseAct.RecordPayment value -> Some value
                | _ -> None)
                (record id |> field "values" id payment)
            unitVariant "CLEAR_PAYMENT" CaseAct.ClearPayment
            unitVariant "CLOSE" CaseAct.Close
            unitVariant "REOPEN" CaseAct.Reopen
        ]

    let private stewardshipActs =
        [
            variant
                "VOID"
                CaseAct.Void
                (function
                | CaseAct.Void value -> Some value
                | _ -> None)
                (record id |> field "reason" id text)
            variant
                "REINSTATE"
                CaseAct.Reinstate
                (function
                | CaseAct.Reinstate value -> Some value
                | _ -> None)
                (record id |> field "reason" id text)
            unitVariant "ERASE" CaseAct.Erase
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
        |> field "kind" _.Kind (tokens [ "HUMAN"; "SERVICE" ])
        |> field "issuer" _.Issuer (text)
        |> field "subject" _.Subject (text)
        |> build
        |> named "Principal"

    let scope =
        tagged
            "Scope"
            "kind"
            [
                unitVariant "INSTALLATION" Scope.Installation
                variant
                    "CASE"
                    Scope.Case
                    (function
                    | Scope.Case value -> Some value
                    | _ -> None)
                    (record id |> field "caseReference" id text)
            ]

    let grantChange =
        (record (fun v0 v1 v2 ->
            {
                GrantChange.Role = v0
                Scope = v1
                Active = v2
            })
        : Fields<GrantChange, _>)
        |> field
            "role"
            _.Role
            (tokens
                [
                    "OWNER"
                    "CASE_READER"
                    "CASE_REGISTRAR"
                    "CASE_HANDLER"
                    "PAYMENT_RECORDER"
                    "DATA_STEWARD"
                    "AUDITOR"
                ])
        |> field "scope" _.Scope (scope)
        |> field "active" _.Active (boolean)
        |> build
        |> named "GrantChange"

    let authorityAct =
        tagged
            "AuthorityAct"
            "kind"
            [
                unitVariant "REGISTER_ACTOR" AuthorityAct.RegisterActor
                variant
                    "SET_GRANT"
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
                     |> field
                         "role"
                         _.Role
                         (tokens
                             [
                                 "OWNER"
                                 "CASE_READER"
                                 "CASE_REGISTRAR"
                                 "CASE_HANDLER"
                                 "PAYMENT_RECORDER"
                                 "DATA_STEWARD"
                                 "AUDITOR"
                             ])
                     |> field "scope" _.Scope scope
                     |> field "active" _.Active boolean)
                variant
                    "SET_ENABLED"
                    AuthorityAct.SetEnabled
                    (function
                    | AuthorityAct.SetEnabled value -> Some value
                    | _ -> None)
                    (record id |> field "enabled" id boolean)
            ]

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
