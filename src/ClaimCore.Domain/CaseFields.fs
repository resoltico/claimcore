namespace ClaimCore.Domain


[<RequireQualifiedAccess>]
type RecordGroup =
    | Registration
    | Decision
    | Payment

[<RequireQualifiedAccess>]
type CaseSection =
    | Registration
    | Decision
    | Payment
    | Work

module CaseSection =
    let vocabulary =
        Vocabulary.ofUnion<CaseSection> "section" TokenCasing.CamelCase None

[<RequireQualifiedAccess>]
type BusinessField =
    | IncidentDate
    | IncidentNotificationDate
    | IncidentCountry
    | IncidentDescription
    | PolicyNumber
    | InsurerClaimReference
    | ClaimantName
    | InsurerName
    | ClaimedAmount
    | ClaimedCurrency
    | CaseReference
    | PaymentDecisionDate
    | PayableAmount
    | PayableCurrency
    | PaymentDate
    | PaidAmount
    | PaidCurrency
    | Status
    | ResponsibleActor
    | HandlingSummary

[<RequireQualifiedAccess>]
type InputTarget =
    | Field of BusinessField
    | OperationId
    | ExpectedRevision
    | Group of RecordGroup
    | Reason
    | PrincipalKind
    | PrincipalIssuer
    | PrincipalSubject
    | ProfileDisplayName
    | ProfileContact
    | DirectorySearch
    | Grants
    | Role
    | Limit
    | Cursor
    | Text
    | ReminderId
    | NoteId
    | NoteText
    | PaymentId
    | ReminderAction
    | ReminderDueDate
    | ExpectedWorkRevision
    | ExpectedReminderRevision

[<NoEquality; NoComparison>]
type FieldDescriptor =
    {
        Field: BusinessField
        Key: string
        Kind: FieldValueKind
        Group: RecordGroup option
        Section: CaseSection option
        AllowsAbsence: bool
        IsAuthored: bool
        Pairs: BusinessField option
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module RecordGroup =
    let vocabulary = Vocabulary.ofUnion<RecordGroup> "group" TokenCasing.CamelCase None
    let token = Vocabulary.token vocabulary

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseStatus =
    let vocabulary =
        Vocabulary.ofUnion<CaseStatus> "status" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module BusinessField =
    let vocabulary =
        Vocabulary.ofUnion<BusinessField> "field" TokenCasing.CamelCase None

    let key = Vocabulary.token vocabulary

    let private descriptor field kind group pairs =
        {
            Field = field
            Key = key field
            Kind = kind
            Group = group
            Section =
                match group with
                | Some RecordGroup.Registration -> Some CaseSection.Registration
                | Some RecordGroup.Decision -> Some CaseSection.Decision
                | Some RecordGroup.Payment -> Some CaseSection.Payment
                | None -> None
            AllowsAbsence = false
            IsAuthored = field <> BusinessField.Status
            Pairs = pairs
        }
    // Domain owns value structure and amount/currency relationships.
    let describe field =
        match field with
        | BusinessField.IncidentDate ->
            descriptor field FieldValueKind.CalendarDate (Some RecordGroup.Registration) None
        | BusinessField.IncidentNotificationDate ->
            descriptor field FieldValueKind.CalendarDate (Some RecordGroup.Registration) None
        | BusinessField.IncidentDescription ->
            { descriptor
                  field
                  FieldValueKind.IncidentDescription
                  (Some RecordGroup.Registration)
                  None with
                AllowsAbsence = true
            }
        | BusinessField.PolicyNumber
        | BusinessField.InsurerClaimReference ->
            { descriptor field FieldValueKind.Identifier (Some RecordGroup.Registration) None with
                AllowsAbsence = true
            }
        | BusinessField.IncidentCountry ->
            descriptor field FieldValueKind.Country (Some RecordGroup.Registration) None
        | BusinessField.ClaimantName ->
            descriptor field FieldValueKind.PartyName (Some RecordGroup.Registration) None
        | BusinessField.InsurerName ->
            descriptor field FieldValueKind.PartyName (Some RecordGroup.Registration) None
        | BusinessField.ClaimedAmount ->
            descriptor
                field
                FieldValueKind.Amount
                (Some RecordGroup.Registration)
                (Some BusinessField.ClaimedCurrency)
        | BusinessField.ClaimedCurrency ->
            descriptor field FieldValueKind.Currency (Some RecordGroup.Registration) None
        | BusinessField.CaseReference -> descriptor field FieldValueKind.Reference None None
        | BusinessField.PaymentDecisionDate ->
            descriptor field FieldValueKind.CalendarDate (Some RecordGroup.Decision) None
        | BusinessField.PayableAmount ->
            descriptor
                field
                FieldValueKind.Amount
                (Some RecordGroup.Decision)
                (Some BusinessField.PayableCurrency)
        | BusinessField.PayableCurrency ->
            descriptor field FieldValueKind.Currency (Some RecordGroup.Decision) None
        | BusinessField.PaymentDate ->
            descriptor field FieldValueKind.CalendarDate (Some RecordGroup.Payment) None
        | BusinessField.PaidAmount ->
            descriptor
                field
                FieldValueKind.Amount
                (Some RecordGroup.Payment)
                (Some BusinessField.PaidCurrency)
        | BusinessField.PaidCurrency ->
            descriptor field FieldValueKind.Currency (Some RecordGroup.Payment) None
        | BusinessField.Status -> descriptor field FieldValueKind.Status None None
        | BusinessField.ResponsibleActor
        | BusinessField.HandlingSummary ->
            let kind =
                if field = BusinessField.ResponsibleActor then
                    FieldValueKind.ActorId
                else
                    FieldValueKind.HandlingSummary

            { descriptor field kind None None with
                Section = Some CaseSection.Work
                AllowsAbsence = true
            }

    let declarations = Vocabulary.all vocabulary |> List.map describe

    let ofGroup group =
        declarations
        |> List.filter (fun field -> field.Group = Some group)
        |> List.map _.Field

type GroupValues<'stage> = private GroupValues of RecordGroup * Map<BusinessField, 'stage>

module GroupValues =
    let private fields = BusinessField.ofGroup

    let create group members =
        let expected = fields group |> Set.ofList
        let supplied = members |> List.map fst

        let required =
            expected |> Set.filter (BusinessField.describe >> _.AllowsAbsence >> not)

        if
            supplied.Length <> (Set.ofList supplied).Count
            || not (Set.isSubset (Set.ofList supplied) expected)
            || not (Set.isSubset required (Set.ofList supplied))
        then
            Error
                "A group requires its declared mandatory fields and no duplicate or undeclared members."
        else
            Ok(GroupValues(group, Map.ofList members))

    let items (GroupValues(group, values)) =
        fields group
        |> List.choose (fun field ->
            Map.tryFind field values |> Option.map (fun value -> field, value))

    let tryValue field (GroupValues(_, values)) = Map.tryFind field values

    let raw field values =
        tryValue field values |> Option.get |> Authored.format

    let createAuthored group members =
        members
        |> List.map (fun (field, value) -> field, Authored.create value)
        |> create group

    let parse values =
        items values
        |> List.map (fun (field, value) ->
            field, FieldValue.parse (BusinessField.describe field).Kind (Authored.format value))

    /// Admit independently parsed values before attempting to construct a complete group.
    let check admit parsed =
        parsed
        |> List.map (fun (field, value) -> field, value |> Result.bind (admit field))

    let errors parsed =
        parsed
        |> List.choose (fun (field, result) ->
            match result with
            | Ok _ -> None
            | Error problem -> Some(field, problem))

    let admitted parsed =
        parsed
        |> List.map (fun (field, result) ->
            field,
            Result.defaultWith
                (fun _ -> invalidOp "Parsing must succeed before constructing admitted values.")
                result)

    let merge replacements members =
        List.fold
            (fun values (field, value) -> Map.add field value values)
            (Map.ofList members)
            replacements
        |> Map.toList

type RegistrationInput = private RegistrationInput of GroupValues<Authored>
type Registration = private Registration of GroupValues<Admitted>

module RegistrationInput =
    let value field (RegistrationInput values) = GroupValues.raw field values

    let tryValue field (RegistrationInput values) =
        GroupValues.tryValue field values |> Option.map Authored.format

    let create members =
        GroupValues.createAuthored RecordGroup.Registration members
        |> Result.map RegistrationInput

    let description value =
        tryValue BusinessField.IncidentDescription value

    let values (RegistrationInput values) =
        GroupValues.items values
        |> List.map (fun (field, value) -> field, Authored.format value)

    let replace members value =
        GroupValues.merge members (values value) |> create

    let parsed (RegistrationInput values) = GroupValues.parse values

module Registration =
    let values (Registration values) = GroupValues.items values

    let private requiredValue field (Registration values) =
        GroupValues.tryValue field values |> Option.get

    let private optional field (Registration values) = GroupValues.tryValue field values

    let incidentDate record =
        requiredValue BusinessField.IncidentDate record |> FieldValue.date

    let incidentNotificationDate record =
        requiredValue BusinessField.IncidentNotificationDate record |> FieldValue.date

    let country record =
        requiredValue BusinessField.IncidentCountry record |> FieldValue.country

    let claimant record =
        requiredValue BusinessField.ClaimantName record |> FieldValue.name

    let insurer record =
        requiredValue BusinessField.InsurerName record |> FieldValue.name

    let claimed record =
        Money.create
            (requiredValue BusinessField.ClaimedAmount record |> FieldValue.amount)
            (requiredValue BusinessField.ClaimedCurrency record |> FieldValue.currency)

    let description record =
        optional BusinessField.IncidentDescription record
        |> Option.map FieldValue.description

    let policyNumber record =
        optional BusinessField.PolicyNumber record |> Option.map FieldValue.identifier

    let insurerClaimReference record =
        optional BusinessField.InsurerClaimReference record
        |> Option.map FieldValue.identifier

    let create members =
        GroupValues.create RecordGroup.Registration members
        |> Result.bind (fun values ->
            if
                GroupValues.items values
                |> List.exists (fun (field, value) ->
                    FieldValue.kind value <> (BusinessField.describe field).Kind)
            then
                Error "A field requires its declared value kind."
            else
                let registration = Registration values

                if incidentDate registration > incidentNotificationDate registration then
                    Error "Incident must be on or before notification."
                else
                    Ok registration)

    let replace members value =
        GroupValues.merge members (values value) |> create

    let withDescription description value =
        (values value |> List.filter (fst >> (<>) BusinessField.IncidentDescription))
        @ (description
           |> Option.map (fun value ->
               BusinessField.IncidentDescription, Admitted.Description value)
           |> Option.toList)
        |> create
        |> Result.defaultWith invalidOp

    let authored value =
        values value
        |> List.map (fun (field, value) -> field, FieldValue.format value)
        |> RegistrationInput.create
        |> Result.defaultWith invalidOp

    let private order parsed =
        let date field =
            parsed
            |> List.find (fst >> (=) field)
            |> snd
            |> Result.toOption
            |> Option.map FieldValue.date

        match date BusinessField.IncidentDate, date BusinessField.IncidentNotificationDate with
        | Some incident, Some notification when incident > notification ->
            [ BusinessField.IncidentNotificationDate, Violation.ChronologicalOrder ]
        | _ -> []

    let internal parsePreparedWith admit parsed =
        let admitted = GroupValues.check admit parsed
        let errors = GroupValues.errors admitted @ order parsed

        if List.isEmpty errors then
            GroupValues.admitted admitted |> create |> Result.defaultWith invalidOp |> Ok
        else
            Error(List.sortBy fst errors)

    let internal parseWith admit input =
        RegistrationInput.parsed input |> parsePreparedWith admit

    let parse input = parseWith (fun _ -> Ok) input

type DecisionInput = private DecisionInput of GroupValues<Authored>

type Decision =
    private
        {
            Date: CalendarDate
            Payable: Money
        }

module DecisionInput =
    let value field (DecisionInput values) = GroupValues.raw field values

    let create members =
        GroupValues.createAuthored RecordGroup.Decision members
        |> Result.map DecisionInput

    let values (DecisionInput values) =
        GroupValues.items values
        |> List.map (fun (field, value) -> field, Authored.format value)

    let replace members value =
        GroupValues.merge members (values value) |> create

    let parsed (DecisionInput values) = GroupValues.parse values

module Decision =
    let date (record: Decision) = record.Date
    let payable (record: Decision) = record.Payable

    let create members =
        match List.sortBy fst members with
        | [ BusinessField.PaymentDecisionDate, Admitted.Date date
            BusinessField.PayableAmount, Admitted.Amount amount
            BusinessField.PayableCurrency, Admitted.Currency currency ] ->
            Ok
                {
                    Date = date
                    Payable = Money.create amount currency
                }
        | _ -> Error "Invalid decision members."

    let values (record: Decision) =
        [
            BusinessField.PaymentDecisionDate, Admitted.Date record.Date
            BusinessField.PayableAmount, Admitted.Amount(Money.amount record.Payable)
            BusinessField.PayableCurrency, Admitted.Currency(Money.currencyValue record.Payable)
        ]

    let replace members value =
        GroupValues.merge members (values value) |> create

    let authored value =
        values value
        |> List.map (fun (field, value) -> field, FieldValue.format value)
        |> DecisionInput.create
        |> Result.defaultWith invalidOp

    let internal parsePreparedWith admit prepared =
        let parsed = prepared |> GroupValues.check admit
        let errors = GroupValues.errors parsed

        if List.isEmpty errors then
            GroupValues.admitted parsed |> create |> Result.defaultWith invalidOp |> Ok
        else
            Error(List.sortBy fst errors)

    let internal parseWith admit input =
        DecisionInput.parsed input |> parsePreparedWith admit

    let parse input = parseWith (fun _ -> Ok) input

type PaymentInput = private PaymentInput of GroupValues<Authored>

module private PaymentAmount =
    let validate amount =
        if Amount.value amount > 0M then
            Ok amount
        else
            Error Violation.PaymentAmountPositive

type Payment =
    private
        {
            Date: CalendarDate
            Paid: Money
        }

module PaymentInput =
    let value field (PaymentInput values) = GroupValues.raw field values

    let create members =
        GroupValues.createAuthored RecordGroup.Payment members
        |> Result.map PaymentInput

    let values (PaymentInput values) =
        GroupValues.items values
        |> List.map (fun (field, value) -> field, Authored.format value)

    let replace members value =
        GroupValues.merge members (values value) |> create

    let parsed (PaymentInput values) = GroupValues.parse values

module Payment =
    let date (record: Payment) = record.Date
    let paid (record: Payment) = record.Paid

    let create members =
        match List.sortBy fst members with
        | [ BusinessField.PaymentDate, Admitted.Date date
            BusinessField.PaidAmount, Admitted.Amount amount
            BusinessField.PaidCurrency, Admitted.Currency currency ] ->
            PaymentAmount.validate amount
            |> Result.map (fun amount ->
                {
                    Date = date
                    Paid = Money.create amount currency
                })
            |> Result.mapError (fun _ -> "A payment amount must be positive.")
        | _ -> Error "Invalid payment members."

    let values (record: Payment) =
        [
            BusinessField.PaymentDate, Admitted.Date record.Date
            BusinessField.PaidAmount, Admitted.Amount(Money.amount record.Paid)
            BusinessField.PaidCurrency, Admitted.Currency(Money.currencyValue record.Paid)
        ]

    let replace members value =
        GroupValues.merge members (values value) |> create

    let authored value =
        values value
        |> List.map (fun (field, value) -> field, FieldValue.format value)
        |> PaymentInput.create
        |> Result.defaultWith invalidOp

    let internal parsePreparedWith admit prepared =
        let parsed =
            prepared
            |> List.map (fun (field, result) ->
                field,
                result
                |> Result.bind (function
                    | Admitted.Amount amount ->
                        PaymentAmount.validate amount |> Result.map Admitted.Amount
                    | value -> Ok value))
            |> GroupValues.check admit

        let errors = GroupValues.errors parsed

        if List.isEmpty errors then
            GroupValues.admitted parsed |> create |> Result.defaultWith invalidOp |> Ok
        else
            Error(List.sortBy fst errors)

    let internal parseWith admit input =
        PaymentInput.parsed input |> parsePreparedWith admit

    let parse input = parseWith (fun _ -> Ok) input

/// The accepting operation identifies an entry; its currency belongs to the retained decision.
type PaymentEntry =
    private
        {
            Id: OperationId
            Date: CalendarDate
            Amount: Amount
        }

module PaymentEntry =
    let id entry = entry.Id
    let date entry = entry.Date
    let amount entry = entry.Amount

    let create id date amount =
        PaymentAmount.validate amount
        |> Result.map (fun amount ->
            {
                Id = id
                Date = date
                Amount = amount
            })
        |> Result.mapError (fun _ -> "A payment entry amount must be positive.")

    let recorded id payment =
        {
            Id = id
            Date = Payment.date payment
            Amount = Money.amount (Payment.paid payment)
        }
