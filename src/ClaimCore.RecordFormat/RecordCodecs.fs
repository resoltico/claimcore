namespace ClaimCore.RecordFormat

open ClaimCore.Contract
open ClaimCore.Json


module internal RecordCodecs =
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
                (CaseActKinds.token (CaseActKind.Business BusinessActKind.Correct))
                CaseAct.Correct
                (function
                | CaseAct.Correct value -> Some value
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

    let caseAct = tagged "CaseAct" (businessActs @ stewardshipActs)

    let caseRequestFields =
        (record (fun v0 v1 v2 v3 () ->
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

    let authorityVariants role =
        let grant =
            (record (fun role scope ->
                {
                    GrantChange.Role = role
                    Scope = scope
                })
            : Fields<GrantChange, _>)
            |> field "role" _.Role role
            |> field "scope" _.Scope scope

        [
            unitVariant (AuthorityKinds.token AuthorityKind.Register) AuthorityAct.Register
            variant
                (AuthorityKinds.token AuthorityKind.Grant)
                AuthorityAct.Grant
                (function
                | AuthorityAct.Grant value -> Some value
                | _ -> None)
                grant
            variant
                (AuthorityKinds.token AuthorityKind.Revoke)
                AuthorityAct.Revoke
                (function
                | AuthorityAct.Revoke value -> Some value
                | _ -> None)
                grant
            unitVariant (AuthorityKinds.token AuthorityKind.Enable) AuthorityAct.Enable
            unitVariant (AuthorityKinds.token AuthorityKind.Disable) AuthorityAct.Disable
        ]


    let caseOperation =
        let version = int64 RecordVersions.CaseRequest

        let fields =
            caseRequestFields
            |> field
                "format"
                (fun _ -> ())
                (integer version version |> map (fun _ -> ()) (fun () -> version))

        tagged "CaseRequestRecord.Subject" [ variant "CASE" id Some fields ]

    let caseFields =
        (record (fun reference status registration decision payment ->
            {
                CaseFields.CaseReference = reference
                Status = status
                Registration = registration
                Decision = decision
                Payment = payment
            })
        : Fields<CaseFields, _>)
        |> field "caseReference" _.CaseReference text
        |> field "status" _.Status (enumeration (Vocabulary.entries CaseStatuses.vocabulary))
        |> field "registration" _.Registration registration
        |> field "decision" _.Decision (nullable decision)
        |> field "payment" _.Payment (nullable payment)
        |> build

    let liveState =
        let fields = record id |> field "fields" id caseFields

        tagged
            "SnapshotRecord.State"
            [
                variant
                    "ACTIVE"
                    LiveCase.Active
                    (function
                    | LiveCase.Active fields -> Some fields
                    | _ -> None)
                    fields
                variant
                    "VOIDED"
                    LiveCase.Voided
                    (function
                    | LiveCase.Voided fields -> Some fields
                    | _ -> None)
                    fields
            ]
