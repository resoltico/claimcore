namespace ClaimCore.RecordFormat

open System
open ClaimCore.Domain
open ClaimCore.Json


module internal RecordCodecs =
    open Codec

    let private tagged name variants =
        Codec.taggedOn DurableNames.Kind name variants

    let revision = integer 0L JsonValue.MaximumInteger
    let sequence = integer 1L JsonValue.MaximumInteger

    let private inputField name get fields =
        Codec.field (DurableNames.field name) get Codec.text fields

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
                unitVariant (DurableNames.correction CorrectionGroupAction.Keep) Amendment.Keep
                variant
                    (DurableNames.correction CorrectionGroupAction.Replace)
                    Amendment.Replace
                    (function
                    | Amendment.Replace value -> Some value
                    | _ -> None)
                    (record id |> field DurableNames.Values id registration)
            ]

    let decisionGroup =
        tagged
            "DecisionGroup"
            [
                unitVariant (DurableNames.correction CorrectionGroupAction.Keep) Change.Keep
                variant
                    (DurableNames.correction CorrectionGroupAction.Replace)
                    Change.Replace
                    (function
                    | Change.Replace value -> Some value
                    | _ -> None)
                    (record id |> field DurableNames.Values id decision)
                unitVariant (DurableNames.correction CorrectionGroupAction.Clear) Change.Clear
            ]

    let paymentGroup =
        tagged
            "PaymentGroup"
            [
                unitVariant (DurableNames.correction CorrectionGroupAction.Keep) Change.Keep
                variant
                    (DurableNames.correction CorrectionGroupAction.Replace)
                    Change.Replace
                    (function
                    | Change.Replace value -> Some value
                    | _ -> None)
                    (record id |> field DurableNames.Values id payment)
                unitVariant (DurableNames.correction CorrectionGroupAction.Clear) Change.Clear
            ]

    let correction =
        (record (fun v0 v1 v2 ->
            {
                AuthoredCorrection.Registration = v0
                Decision = v1
                Payment = v2
            })
        : Fields<AuthoredCorrection, _>)
        |> field DurableNames.Registration _.Registration (registrationGroup)
        |> field DurableNames.Decision _.Decision (decisionGroup)
        |> field DurableNames.Payment _.Payment (paymentGroup)
        |> build
        |> named "Correction"

    let private caseCodec (registration, decision, payment) (reason, correction) =
        DurableNames.caseKinds
        |> List.map (fun kind ->
            let tag = DurableNames.caseAct kind

            let memberCodec name codec (inject, project) =
                variant tag inject project (record id |> field name id codec)

            match CaseAct.slot kind with
            | CaseActSlot.Bare act -> unitVariant tag act
            | CaseActSlot.Registration(inject, project) ->
                memberCodec DurableNames.Values registration (inject, project)
            | CaseActSlot.Decision(inject, project) ->
                memberCodec DurableNames.Values decision (inject, project)
            | CaseActSlot.Payment(inject, project) ->
                memberCodec DurableNames.Values payment (inject, project)
            | CaseActSlot.Reason(inject, project) ->
                memberCodec DurableNames.Reason reason (inject, project)
            | CaseActSlot.Correction(inject, project) ->
                memberCodec DurableNames.Groups correction (inject, project))
        |> tagged "CaseAct"

    let caseAct = caseCodec (registration, decision, payment) (text, correction)

    let caseRequestFields =
        (record (fun v0 v1 v2 v3 () ->
            {
                UnvalidatedCaseRequest.OperationId = v0
                CaseReference = v1
                ExpectedRevision = v2
                Act = v3
            })
        : Fields<UnvalidatedCaseRequest, _>)
        |> field DurableNames.OperationId _.OperationId (uuid)
        |> field DurableNames.CaseReference _.CaseReference (text)
        |> field DurableNames.ExpectedRevision _.ExpectedRevision (revision)
        |> field DurableNames.Act _.Act (caseAct)

    let principal =
        (record (fun v0 v1 v2 ->
            {
                PrincipalInput.Kind = v0
                Issuer = v1
                Subject = v2
            })
        : Fields<PrincipalInput, _>)
        |> field DurableNames.Kind _.Kind (enumeration DurableNames.principalKindEntries)
        |> field DurableNames.Issuer _.Issuer (text)
        |> field DurableNames.Subject _.Subject (text)
        |> build
        |> named "Principal"

    let scope =
        tagged
            "Scope"
            [
                unitVariant DurableNames.InstallationScope GrantTarget.Installation
                variant
                    DurableNames.CaseScope
                    GrantTarget.CaseReference
                    (function
                    | GrantTarget.CaseReference value -> Some value
                    | _ -> None)
                    (record id |> field DurableNames.CaseReference id text)
            ]

    let private authorityVariantsWith role scope =
        let grant =
            record (fun role scope ->
                {
                    AuthoredGrant.Role = role
                    Scope = scope
                })
            |> field DurableNames.Role _.Role role
            |> field DurableNames.Scope _.Scope scope

        DurableNames.authorityKinds
        |> List.filter ((<>) AuthorityKind.ProvisionOwner)
        |> List.map (fun kind ->
            let token = DurableNames.authorityAct kind

            match AuthorityAct.slot kind with
            | AuthorityActSlot.Bare act -> unitVariant token act
            | AuthorityActSlot.Grant(inject, project) -> variant token inject project grant)

    let role = enumeration DurableNames.roleEntries
    let authorityVariants role = authorityVariantsWith role scope

    let caseOperation =
        let version = int64 RecordVersions.CaseRequest

        let fields =
            caseRequestFields
            |> field
                DurableNames.Format
                (fun _ -> ())
                (integer version version |> map (fun _ -> ()) (fun () -> version))

        tagged "CaseRequestRecord.Subject" [ variant DurableNames.CaseSubject id Some fields ]

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
        |> field DurableNames.IncidentDate _.IncidentDate acceptedDate
        |> field DurableNames.IncidentNotificationDate _.IncidentNotificationDate acceptedDate
        |> field DurableNames.IncidentCountry _.IncidentCountry acceptedCountry
        |> field DurableNames.ClaimantName _.ClaimantName acceptedName
        |> field DurableNames.InsurerName _.InsurerName acceptedName
        |> field DurableNames.ClaimedAmount (fun value -> Money.format value.ClaimedAmount) text
        |> field DurableNames.ClaimedCurrency (fun value -> Money.currency value.ClaimedAmount) text
        |> buildResult (moneyProblem DurableNames.ClaimedAmount DurableNames.ClaimedCurrency)
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
        |> field DurableNames.PaymentDecisionDate _.PaymentDecisionDate acceptedDate
        |> field DurableNames.PayableAmount (fun value -> Money.format value.PayableAmount) text
        |> field DurableNames.PayableCurrency (fun value -> Money.currency value.PayableAmount) text
        |> buildResult (moneyProblem DurableNames.PayableAmount DurableNames.PayableCurrency)
        |> named "Decision"

    let private acceptedPayment =
        (record (fun date -> { Payment.PaymentDate = date }): Fields<Payment, _>)
        |> field DurableNames.PaymentDate _.PaymentDate acceptedDate
        |> build
        |> named "Payment"

    let caseFields =
        (record (fun reference status registration decision payment ->
            PaymentProgress.fromGroups decision (payment |> Option.map _.PaymentDate)
            |> Result.map (fun progress ->
                {
                    CaseFields.CaseReference = reference
                    Status = status
                    Registration = registration
                    Progress = progress
                }))
        : Fields<CaseFields, _>)
        |> field DurableNames.CaseReference _.CaseReference acceptedReference
        |> field DurableNames.Status _.Status (enumeration (DurableNames.statusEntries))
        |> field DurableNames.Registration _.Registration acceptedRegistration
        |> field DurableNames.Decision _.Decision (nullable acceptedDecision)
        |> field DurableNames.Payment _.Payment (nullable acceptedPayment)
        |> buildResult (fun path () ->
            {
                Path = path + ".payment"
                Reason = "Payment requires a decision."
            })

    let liveState =
        let fields = record id |> field DurableNames.Fields id caseFields

        tagged
            "SnapshotRecord.State"
            [
                variant
                    (DurableNames.lifecycle Lifecycle.Active)
                    LiveCase.Active
                    (function
                    | LiveCase.Active fields -> Some fields
                    | _ -> None)
                    fields
                variant
                    (DurableNames.lifecycle Lifecycle.Voided)
                    LiveCase.Voided
                    (function
                    | LiveCase.Voided fields -> Some fields
                    | _ -> None)
                    fields
            ]
