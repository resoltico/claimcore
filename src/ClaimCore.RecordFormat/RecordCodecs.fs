namespace ClaimCore.RecordFormat

open System
open ClaimCore.Domain
open ClaimCore.Json


module internal RecordCodecs =
    open Codec

    let revision = integer 0L JsonValue.MaximumInteger
    let sequence = integer 1L JsonValue.MaximumInteger

    let tokens values =
        values |> List.map (fun value -> value, value) |> enumeration

    let private inputField name get fields =
        Codec.field (BusinessField.key name) get Codec.text fields

    let registration =
        (Codec.record (fun incident notification country claimant insurer amount currency ->
            {
                RegistrationInput.IncidentDate = incident
                IncidentNotificationDate = notification
                IncidentCountry = country
                ClaimantName = claimant
                InsurerName = insurer
                ClaimedAmount = amount
                ClaimedCurrency = currency
            })
        : Fields<RegistrationInput, _>)
        |> inputField BusinessField.IncidentDate _.IncidentDate
        |> inputField BusinessField.IncidentNotificationDate _.IncidentNotificationDate
        |> inputField BusinessField.IncidentCountry _.IncidentCountry
        |> inputField BusinessField.ClaimantName _.ClaimantName
        |> inputField BusinessField.InsurerName _.InsurerName
        |> inputField BusinessField.ClaimedAmount _.ClaimedAmount
        |> inputField BusinessField.ClaimedCurrency _.ClaimedCurrency
        |> Codec.build
        |> Codec.named "Registration"

    let decision =
        (Codec.record (fun date amount currency ->
            {
                DecisionInput.PaymentDecisionDate = date
                PayableAmount = amount
                PayableCurrency = currency
            })
        : Fields<DecisionInput, _>)
        |> inputField BusinessField.PaymentDecisionDate _.PaymentDecisionDate
        |> inputField BusinessField.PayableAmount _.PayableAmount
        |> inputField BusinessField.PayableCurrency _.PayableCurrency
        |> Codec.build
        |> Codec.named "Decision"

    let payment =
        (Codec.record (fun date -> { PaymentInput.PaymentDate = date }): Fields<PaymentInput, _>)
        |> inputField BusinessField.PaymentDate _.PaymentDate
        |> Codec.build
        |> Codec.named "Payment"

    let registrationGroup =
        tagged
            "RegistrationGroup"
            [
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Keep) Change.Keep
                variant
                    (CorrectionGroupAction.token CorrectionGroupAction.Replace)
                    Change.Replace
                    (function
                    | Change.Replace value -> Some value
                    | _ -> None)
                    (record id |> field "values" id registration)
            ]

    let decisionGroup =
        tagged
            "DecisionGroup"
            [
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Keep) Change.Keep
                variant
                    (CorrectionGroupAction.token CorrectionGroupAction.Replace)
                    Change.Replace
                    (function
                    | Change.Replace value -> Some value
                    | _ -> None)
                    (record id |> field "values" id decision)
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Clear) Change.Clear
            ]

    let paymentGroup =
        tagged
            "PaymentGroup"
            [
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Keep) Change.Keep
                variant
                    (CorrectionGroupAction.token CorrectionGroupAction.Replace)
                    Change.Replace
                    (function
                    | Change.Replace value -> Some value
                    | _ -> None)
                    (record id |> field "values" id payment)
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Clear) Change.Clear
            ]

    let correction =
        (record (fun v0 v1 v2 ->
            {
                AuthoredCorrection.Registration = v0
                Decision = v1
                Payment = v2
            })
        : Fields<AuthoredCorrection, _>)
        |> field "registration" _.Registration (registrationGroup)
        |> field "decision" _.Decision (decisionGroup)
        |> field "payment" _.Payment (paymentGroup)
        |> build
        |> named "Correction"

    let private caseCodec registration decision payment reason correction =
        let memberCodec name codec inject project kind =
            variant
                (CaseActKind.token kind)
                (inject >> CaseAct.ofPayload kind)
                (fun act ->
                    if CaseAct.kind act = kind then
                        project (CaseAct.payload act)
                    else
                        None)
                (record id |> field name id codec)

        CaseActKind.all
        |> List.map (fun kind ->
            match CaseActKind.shape kind with
            | ActShape.NoValues ->
                unitVariant (CaseActKind.token kind) (CaseAct.ofPayload kind ActPayload.Empty)
            | ActShape.RegistrationValues ->
                memberCodec
                    "values"
                    registration
                    ActPayload.Registration
                    ActPayload.registration
                    kind
            | ActShape.DecisionValues ->
                memberCodec "values" decision ActPayload.Decision ActPayload.decision kind
            | ActShape.PaymentValues ->
                memberCodec "values" payment ActPayload.Payment ActPayload.payment kind
            | ActShape.ReasonText ->
                memberCodec "reason" reason ActPayload.Reason ActPayload.reason kind
            | ActShape.CorrectionGroups ->
                memberCodec "groups" correction ActPayload.Correction ActPayload.correction kind)
        |> tagged "CaseAct"

    let caseAct = caseCodec registration decision payment text correction

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
        |> field "kind" _.Kind (tokens PrincipalKind.all)
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

    let private authorityVariantsWith role scope =
        let grant =
            record (fun role scope ->
                {
                    GrantChange.Role = role
                    Scope = scope
                })
            |> field "role" _.Role role
            |> field "scope" _.Scope scope

        AuthorityKind.all
        |> List.filter ((<>) AuthorityKind.ProvisionOwner)
        |> List.map (fun kind ->
            let token = AuthorityKind.token kind

            match AuthorityAct.shape kind with
            | AuthorityShape.TargetOnly -> unitVariant token (AuthorityAct.ofPayload kind None)
            | AuthorityShape.GrantValues ->
                variant
                    token
                    (Some >> AuthorityAct.ofPayload kind)
                    (fun act ->
                        if AuthorityAct.kind act = kind then
                            AuthorityAct.payload act
                        else
                            None)
                    grant)

    let authorityVariants role = authorityVariantsWith role scope

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
        |> field "status" _.Status (enumeration (Vocabulary.entries CaseStatus.vocabulary))
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
