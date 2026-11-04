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

    // Frozen member inventory: adding a current domain descriptor cannot change this grammar.
    let private members =
        function
        | RecordGroup.Registration ->
            [
                BusinessField.IncidentDate, FieldValueKind.CalendarDate
                BusinessField.IncidentNotificationDate, FieldValueKind.CalendarDate
                BusinessField.IncidentCountry, FieldValueKind.Country
                BusinessField.ClaimantName, FieldValueKind.PartyName
                BusinessField.InsurerName, FieldValueKind.PartyName
                BusinessField.ClaimedAmount, FieldValueKind.Amount
                BusinessField.ClaimedCurrency, FieldValueKind.Currency
            ]
        | RecordGroup.Decision ->
            [
                BusinessField.PaymentDecisionDate, FieldValueKind.CalendarDate
                BusinessField.PayableAmount, FieldValueKind.Amount
                BusinessField.PayableCurrency, FieldValueKind.Currency
            ]
        | RecordGroup.Payment -> [ BusinessField.PaymentDate, FieldValueKind.CalendarDate ]

    let private inputGroup group create values =
        members group
        |> List.map (fun (field, _) -> field, DurableNames.field field, Codec.text)
        |> Codec.fieldMap
        |> Codec.mapResult
            (fun path fields ->
                create (Map.toList fields)
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (values >> Map.ofList)

    let registration =
        (record (fun incident notification country claimant insurer amount currency description ->
            [
                BusinessField.IncidentDate, incident
                BusinessField.IncidentNotificationDate, notification
                BusinessField.IncidentCountry, country
                BusinessField.ClaimantName, claimant
                BusinessField.InsurerName, insurer
                BusinessField.ClaimedAmount, amount
                BusinessField.ClaimedCurrency, currency
                yield!
                    description
                    |> Option.flatten
                    |> Option.map (fun value -> BusinessField.IncidentDescription, value)
                    |> Option.toList
            ]
            |> RegistrationInput.create
            |> Result.defaultWith invalidOp)
        : Fields<RegistrationInput, _>)
        |> field "incidentDate" (RegistrationInput.value BusinessField.IncidentDate) text
        |> field
            "incidentNotificationDate"
            (RegistrationInput.value BusinessField.IncidentNotificationDate)
            text
        |> field "incidentCountry" (RegistrationInput.value BusinessField.IncidentCountry) text
        |> field "claimantName" (RegistrationInput.value BusinessField.ClaimantName) text
        |> field "insurerName" (RegistrationInput.value BusinessField.InsurerName) text
        |> field "claimedAmount" (RegistrationInput.value BusinessField.ClaimedAmount) text
        |> field "claimedCurrency" (RegistrationInput.value BusinessField.ClaimedCurrency) text
        |> optionalField
            "incidentDescription"
            (RegistrationInput.description >> Option.map Some)
            (nullable text)
        |> build
        |> named "Registration"

    let decision =
        inputGroup RecordGroup.Decision DecisionInput.create DecisionInput.values
        |> Codec.named "Decision"

    let payment =
        inputGroup RecordGroup.Payment PaymentInput.create PaymentInput.values
        |> Codec.named "Payment"


    let private moneyInput =
        (record (fun amount currency ->
            {
                MoneyInput.Amount = amount
                Currency = currency
            })
        : Fields<MoneyInput, _>)
        |> field "amount" _.Amount text
        |> field "currency" _.Currency text
        |> build

    let registrationUpdate =
        (record (fun incident notification country claimant insurer claimed description ->
            {
                RegistrationUpdateInput.IncidentDate = incident
                NotificationDate = notification
                Country = country
                Claimant = claimant
                Insurer = insurer
                Claimed = claimed
                IncidentDescription =
                    match description with
                    | None -> WorkValue.Keep
                    | Some None -> WorkValue.Clear
                    | Some(Some value) -> WorkValue.Set value
            })
        : Fields<RegistrationUpdateInput, _>)
        |> optionalField "incidentDate" _.IncidentDate text
        |> optionalField "incidentNotificationDate" _.NotificationDate text
        |> optionalField "incidentCountry" _.Country text
        |> optionalField "claimantName" _.Claimant text
        |> optionalField "insurerName" _.Insurer text
        |> optionalField "claimed" _.Claimed moneyInput
        |> optionalField
            "incidentDescription"
            (fun value ->
                match value.IncidentDescription with
                | WorkValue.Keep -> None
                | WorkValue.Clear -> Some None
                | WorkValue.Set text -> Some(Some text))
            (nullable text)
        |> build
        |> named "RegistrationUpdate"

    let decisionUpdate =
        (record (fun date payable ->
            {
                DecisionUpdateInput.Date = date
                Payable = payable
            })
        : Fields<DecisionUpdateInput, _>)
        |> optionalField "paymentDecisionDate" _.Date text
        |> optionalField "payable" _.Payable moneyInput
        |> build
        |> named "DecisionUpdate"

    let registrationGroup =
        tagged
            "RegistrationGroup"
            [
                unitVariant (DurableNames.correction CorrectionGroupAction.Keep) Amendment.Keep
                variant
                    (DurableNames.correction CorrectionGroupAction.Update)
                    Amendment.Update
                    (function
                    | Amendment.Update value -> Some value
                    | _ -> None)
                    (record id |> field DurableNames.Values id registrationUpdate)
            ]

    let decisionGroup =
        tagged
            "DecisionGroup"
            [
                unitVariant (DurableNames.correction CorrectionGroupAction.Keep) Change.Keep
                variant
                    (DurableNames.correction CorrectionGroupAction.Update)
                    Change.Update
                    (function
                    | Change.Update value -> Some value
                    | _ -> None)
                    (record id |> field DurableNames.Values id decisionUpdate)
                unitVariant (DurableNames.correction CorrectionGroupAction.Clear) Change.Clear
            ]

    let paymentGroup =
        tagged
            "PaymentGroup"
            [
                unitVariant (DurableNames.correction CorrectionGroupAction.Keep) PaymentChange.Keep
                variant
                    (DurableNames.correction CorrectionGroupAction.Replace)
                    PaymentChange.Replace
                    (function
                    | PaymentChange.Replace value -> Some value
                    | _ -> None)
                    (record id |> field DurableNames.Values id payment)
                unitVariant "REAFFIRM" PaymentChange.Reaffirm
                unitVariant
                    (DurableNames.correction CorrectionGroupAction.Clear)
                    PaymentChange.Clear
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
            | CaseActSlot.RegistrationUpdate(inject, project) ->
                memberCodec "values" registrationUpdate (inject, project)
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
        let caseId = uuid |> map (CaseId.create >> Option.get) CaseId.value

        let reference =
            text
            |> mapResult
                (fun path raw ->
                    CaseReference.parse raw
                    |> Result.mapError (fun _ ->
                        {
                            Path = path
                            Reason = "Invalid retained reference."
                        }))
                CaseReference.format

        tagged
            "RecordedGrantScope"
            [
                unitVariant DurableNames.InstallationScope RecordedGrantScope.Installation
                variant
                    DurableNames.CaseScope
                    (fun (id, reference) -> RecordedGrantScope.Case(id, reference))
                    (function
                    | RecordedGrantScope.Case(id, reference) -> Some(id, reference)
                    | _ -> None)
                    (record (fun id reference -> id, reference)
                     |> field DurableNames.CaseId fst caseId
                     |> field DurableNames.CaseReference snd reference)
            ]

    let role = enumeration DurableNames.roleEntries

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

    let private acceptedReference = scalar CaseReference.parse CaseReference.format

    let private acceptedGroup group create values =
        members group
        |> List.map (fun (field, kind) ->
            field, DurableNames.field field, scalar (FieldValue.parse kind) FieldValue.format)
        |> Codec.fieldMap
        |> mapResult
            (fun path fields ->
                create (Map.toList fields)
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (values >> Map.ofList)

    let private acceptedRegistration =
        (record (fun incident notification country claimant insurer amount currency description ->
            [
                BusinessField.IncidentDate, incident
                BusinessField.IncidentNotificationDate, notification
                BusinessField.IncidentCountry, country
                BusinessField.ClaimantName, claimant
                BusinessField.InsurerName, insurer
                BusinessField.ClaimedAmount, amount
                BusinessField.ClaimedCurrency, currency
                yield!
                    description
                    |> Option.map (fun value -> BusinessField.IncidentDescription, value)
                    |> Option.toList
            ]
            |> Registration.create)
        : Fields<Registration, _>)
        |> field
            "incidentDate"
            (Registration.incidentDate >> Admitted.Date)
            (scalar (FieldValue.parse FieldValueKind.CalendarDate) FieldValue.format)
        |> field
            "incidentNotificationDate"
            (Registration.incidentNotificationDate >> Admitted.Date)
            (scalar (FieldValue.parse FieldValueKind.CalendarDate) FieldValue.format)
        |> field
            "incidentCountry"
            (Registration.country >> Admitted.Country)
            (scalar (FieldValue.parse FieldValueKind.Country) FieldValue.format)
        |> field
            "claimantName"
            (Registration.claimant >> Admitted.Name)
            (scalar (FieldValue.parse FieldValueKind.PartyName) FieldValue.format)
        |> field
            "insurerName"
            (Registration.insurer >> Admitted.Name)
            (scalar (FieldValue.parse FieldValueKind.PartyName) FieldValue.format)
        |> field
            "claimedAmount"
            (Registration.claimed >> Money.amount >> Admitted.Amount)
            (scalar (FieldValue.parse FieldValueKind.Amount) FieldValue.format)
        |> field
            "claimedCurrency"
            (Registration.claimed >> Money.currencyValue >> Admitted.Currency)
            (scalar (FieldValue.parse FieldValueKind.Currency) FieldValue.format)
        |> field
            "incidentDescription"
            (Registration.description >> Option.map Admitted.Description)
            (nullable (
                scalar (FieldValue.parse FieldValueKind.IncidentDescription) FieldValue.format
            ))
        |> buildResult (fun path reason -> { Path = path; Reason = reason })
        |> named "Registration"

    let private acceptedDecision =
        acceptedGroup RecordGroup.Decision Decision.create Decision.values
        |> named "Decision"

    let private acceptedPayment =
        acceptedGroup RecordGroup.Payment Payment.create Payment.values
        |> named "Payment"

    let caseFields =
        (record (fun reference status registration decision payment ->
            PaymentProgress.fromGroups decision payment
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
