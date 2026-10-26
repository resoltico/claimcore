namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json


module internal RecordCodecs =
    open Codec

    let revision = integer 0L JsonValue.MaximumInteger
    let sequence = integer 1L JsonValue.MaximumInteger

    let tokens values =
        values |> List.map (fun value -> value, value) |> enumeration

    let registration = Record.registration.Codec text
    let decision = Record.decision.Codec text
    let payment = Record.payment.Codec text

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
                CorrectionInput.Registration = v0
                Decision = v1
                Payment = v2
            })
        : Fields<CorrectionInput, _>)
        |> field "registration" _.Registration (registrationGroup)
        |> field "decision" _.Decision (decisionGroup)
        |> field "payment" _.Payment (paymentGroup)
        |> build
        |> named "Correction"

    let caseAct = Record.caseCodec text correction

    let caseRequestFields =
        (record (fun v0 v1 v2 v3 () ->
            {
                UnvalidatedCaseRequest.OperationId = v0
                CaseReference = v1
                ExpectedRevision = v2
                Act = v3
            })
        : Fields<UnvalidatedCaseRequest, _>)
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

    let authorityVariants role = Record.authorityVariants role scope

    let caseOperation =
        let version = int64 RecordVersions.CaseRequest

        let fields =
            caseRequestFields
            |> field
                "format"
                (fun _ -> ())
                (integer version version |> map (fun _ -> ()) (fun () -> version))

        tagged "CaseRequestRecord.Subject" [ variant "CASE" id Some fields ]

    let private scalar parse format =
        text
        |> mapResult
            (fun path raw ->
                parse raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid accepted scalar format."
                    }))
            format

    let private acceptedDate = scalar CalendarDate.parse CalendarDate.format
    let private acceptedReference = scalar CaseReference.parse CaseReference.format
    let private acceptedCountry = scalar CountryCode.parse CountryCode.format
    let private acceptedName = scalar PartyName.parse PartyName.format

    let private moneyProblem amountField currencyField path =
        function
        | MoneyViolation.Amount _ ->
            {
                Path = path + "." + amountField
                Reason = "Invalid accepted amount."
            }
        | MoneyViolation.Currency _ ->
            {
                Path = path + "." + currencyField
                Reason = "Invalid accepted currency."
            }

    let private acceptedRegistration =
        (record (fun incident notification country claimant insurer amount currency ->
            Money.parse amount currency
            |> Result.map (fun money ->
                {
                    Registration.IncidentDate = incident
                    IncidentNotificationDate = notification
                    IncidentCountry = country
                    ClaimantName = claimant
                    InsurerName = insurer
                    ClaimedAmount = money
                }))
        : Fields<Registration, _>)
        |> field "incidentDate" _.IncidentDate acceptedDate
        |> field "incidentNotificationDate" _.IncidentNotificationDate acceptedDate
        |> field "incidentCountry" _.IncidentCountry acceptedCountry
        |> field "claimantName" _.ClaimantName acceptedName
        |> field "insurerName" _.InsurerName acceptedName
        |> field "claimedAmount" (fun value -> Money.format value.ClaimedAmount) text
        |> field "claimedCurrency" (fun value -> Money.currency value.ClaimedAmount) text
        |> buildResult (moneyProblem "claimedAmount" "claimedCurrency")
        |> named "Registration"

    let private acceptedDecision =
        (record (fun date amount currency ->
            Money.parse amount currency
            |> Result.map (fun money ->
                {
                    Decision.PaymentDecisionDate = date
                    PayableAmount = money
                }))
        : Fields<Decision, _>)
        |> field "paymentDecisionDate" _.PaymentDecisionDate acceptedDate
        |> field "payableAmount" (fun value -> Money.format value.PayableAmount) text
        |> field "payableCurrency" (fun value -> Money.currency value.PayableAmount) text
        |> buildResult (moneyProblem "payableAmount" "payableCurrency")
        |> named "Decision"

    let private acceptedPayment =
        (record (fun date -> { Payment.PaymentDate = date }): Fields<Payment, _>)
        |> field "paymentDate" _.PaymentDate acceptedDate
        |> build
        |> named "Payment"

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
        |> field "caseReference" _.CaseReference acceptedReference
        |> field "status" _.Status (enumeration (Vocabulary.entries CaseStatuses.vocabulary))
        |> field "registration" _.Registration acceptedRegistration
        |> field "decision" _.Decision (nullable acceptedDecision)
        |> field "payment" _.Payment (nullable acceptedPayment)
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
