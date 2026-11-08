namespace ClaimCore.RecordFormat

open System
open ClaimCore.Domain
open ClaimCore.Json


module internal RecordCodecs =
    open Codec

    let private tagged name variants = Codec.taggedOn "kind" name variants

    let revision = integer 0L JsonValue.MaximumInteger
    let sequence = integer 1L JsonValue.MaximumInteger

    let private members group =
        CaseFields.ofGroup group
        |> List.map (fun field -> field, (BusinessField.describe field).Kind)

    let caseRequestFields =
        (record (fun v0 v1 v2 v3 ->
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
        |> field "act" _.Act (CanonicalInputs.caseAct)

    let principal = CanonicalInputs.principal

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
                unitVariant "INSTALLATION" RecordedGrantScope.Installation
                variant
                    "CASE"
                    (fun (id, reference) -> RecordedGrantScope.Case(id, reference))
                    (function
                    | RecordedGrantScope.Case(id, reference) -> Some(id, reference)
                    | _ -> None)
                    (record (fun id reference -> id, reference)
                     |> field "caseId" fst caseId
                     |> field "caseReference" snd reference)
            ]

    let role = enumeration (Vocabulary.entries Role.vocabulary)

    let caseOperation =
        tagged "CaseRequestRecord.Subject" [ variant "CASE" id Some caseRequestFields ]

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
            field, BusinessField.key field, scalar (FieldValue.parse kind) FieldValue.format)
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
        |> field "caseReference" _.CaseReference acceptedReference
        |> field "status" _.Status (enumeration ((Vocabulary.entries CaseStatus.vocabulary)))
        |> field "registration" _.Registration acceptedRegistration
        |> field "decision" _.Decision (nullable acceptedDecision)
        |> field "payment" _.Payment (nullable acceptedPayment)
        |> buildResult (fun path () ->
            {
                Path = path + ".payment"
                Reason = "Payment requires a decision."
            })

    let liveState =
        let fields = record id |> field "fields" id caseFields

        tagged
            "SnapshotRecord.State"
            [
                variant
                    (Lifecycle.token Lifecycle.Active)
                    LiveCase.Active
                    (function
                    | LiveCase.Active fields -> Some fields
                    | _ -> None)
                    fields
                variant
                    (Lifecycle.token Lifecycle.Voided)
                    LiveCase.Voided
                    (function
                    | LiveCase.Voided fields -> Some fields
                    | _ -> None)
                    fields
            ]
