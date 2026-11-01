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
    | ClaimantName
    | InsurerName
    | ClaimedAmount
    | ClaimedCurrency
    | CaseReference
    | PaymentDecisionDate
    | PayableAmount
    | PayableCurrency
    | PaymentDate
    | Status
    | ResponsibleActor
    | FollowUpDate

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
    | Role
    | Limit
    | Cursor
    | Text

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
        Searchable: bool
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

    let private descriptor field kind group searchable pairs =
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
            AllowsAbsence = group.IsSome && group <> Some RecordGroup.Registration
            IsAuthored = field <> BusinessField.Status
            Searchable = searchable
            Pairs = pairs
        }
    // One exhaustive table owns field structure, searchability and amount/currency relationships.
    // fsharplint:disable-next-line CyclomaticComplexity
    let describe field =
        match field with
        | BusinessField.IncidentDate ->
            descriptor field FieldValueKind.CalendarDate (Some RecordGroup.Registration) false None
        | BusinessField.IncidentNotificationDate ->
            descriptor field FieldValueKind.CalendarDate (Some RecordGroup.Registration) false None
        | BusinessField.IncidentCountry ->
            descriptor field FieldValueKind.Country (Some RecordGroup.Registration) false None
        | BusinessField.ClaimantName ->
            descriptor field FieldValueKind.PartyName (Some RecordGroup.Registration) true None
        | BusinessField.InsurerName ->
            descriptor field FieldValueKind.PartyName (Some RecordGroup.Registration) true None
        | BusinessField.ClaimedAmount ->
            descriptor
                field
                FieldValueKind.Amount
                (Some RecordGroup.Registration)
                false
                (Some BusinessField.ClaimedCurrency)
        | BusinessField.ClaimedCurrency ->
            descriptor field FieldValueKind.Currency (Some RecordGroup.Registration) false None
        | BusinessField.CaseReference -> descriptor field FieldValueKind.Reference None true None
        | BusinessField.PaymentDecisionDate ->
            descriptor field FieldValueKind.CalendarDate (Some RecordGroup.Decision) false None
        | BusinessField.PayableAmount ->
            descriptor
                field
                FieldValueKind.Amount
                (Some RecordGroup.Decision)
                false
                (Some BusinessField.PayableCurrency)
        | BusinessField.PayableCurrency ->
            descriptor field FieldValueKind.Currency (Some RecordGroup.Decision) false None
        | BusinessField.PaymentDate ->
            descriptor field FieldValueKind.CalendarDate (Some RecordGroup.Payment) false None
        | BusinessField.Status -> descriptor field FieldValueKind.Status None false None
        | BusinessField.ResponsibleActor
        | BusinessField.FollowUpDate ->
            let kind =
                if field = BusinessField.ResponsibleActor then
                    FieldValueKind.ActorId
                else
                    FieldValueKind.PlannedDate

            { descriptor field kind None false None with
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
    // Authored group shape is explicit: adding a descriptor cannot change retained inputs.
    let private fields =
        function
        | RecordGroup.Registration ->
            [
                BusinessField.IncidentDate
                BusinessField.IncidentNotificationDate
                BusinessField.IncidentCountry
                BusinessField.ClaimantName
                BusinessField.InsurerName
                BusinessField.ClaimedAmount
                BusinessField.ClaimedCurrency
            ]
        | RecordGroup.Decision ->
            [
                BusinessField.PaymentDecisionDate
                BusinessField.PayableAmount
                BusinessField.PayableCurrency
            ]
        | RecordGroup.Payment -> [ BusinessField.PaymentDate ]

    let private create group members =
        let expected = fields group
        let supplied = members |> List.map fst

        if
            List.length supplied <> List.length expected
            || Set.ofList supplied <> Set.ofList expected
        then
            Error "A group must contain each declared field exactly once."
        else
            Ok(GroupValues(group, Map.ofList members))

    let items (GroupValues(group, values)) =
        fields group |> List.map (fun field -> field, values[field])

    let raw field (GroupValues(_, values)) = values[field] |> Authored.format

    let createAuthored group members =
        members
        |> List.map (fun (field, value) -> field, Authored.create value)
        |> create group

    let parse values =
        items values
        |> List.map (fun (field, value) ->
            field, FieldValue.parse (BusinessField.describe field).Kind (Authored.format value))

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

type Registration =
    private
        {
            IncidentDate: CalendarDate
            NotificationDate: CalendarDate
            Country: CountryCode
            Claimant: PartyName
            Insurer: PartyName
            Claimed: Money
        }

module RegistrationInput =
    let value field (RegistrationInput values) = GroupValues.raw field values

    let create members =
        GroupValues.createAuthored RecordGroup.Registration members
        |> Result.map RegistrationInput

    let values (RegistrationInput values) =
        GroupValues.items values
        |> List.map (fun (field, value) -> field, Authored.format value)

    let replace members value =
        GroupValues.merge members (values value) |> create

    let parsed (RegistrationInput values) = GroupValues.parse values

module Registration =
    let incidentDate (record: Registration) = record.IncidentDate
    let incidentNotificationDate (record: Registration) = record.NotificationDate
    let country (record: Registration) = record.Country
    let claimant (record: Registration) = record.Claimant
    let insurer (record: Registration) = record.Insurer
    let claimed (record: Registration) = record.Claimed

    /// Adapts a field collection at the transport/presentation boundary.
    let create members =
        match List.sortBy fst members with
        | [ BusinessField.IncidentDate, Admitted.Date incident
            BusinessField.IncidentNotificationDate, Admitted.Date notification
            BusinessField.IncidentCountry, Admitted.Country country
            BusinessField.ClaimantName, Admitted.Name claimant
            BusinessField.InsurerName, Admitted.Name insurer
            BusinessField.ClaimedAmount, Admitted.Amount amount
            BusinessField.ClaimedCurrency, Admitted.Currency currency ] ->
            if incident > notification then
                Error "Incident must be on or before notification."
            else
                Ok
                    {
                        IncidentDate = incident
                        NotificationDate = notification
                        Country = country
                        Claimant = claimant
                        Insurer = insurer
                        Claimed = Money.create amount currency
                    }
        | _ -> Error "Invalid registration members."

    let values (record: Registration) =
        [
            BusinessField.IncidentDate, Admitted.Date record.IncidentDate
            BusinessField.IncidentNotificationDate, Admitted.Date record.NotificationDate
            BusinessField.IncidentCountry, Admitted.Country record.Country
            BusinessField.ClaimantName, Admitted.Name record.Claimant
            BusinessField.InsurerName, Admitted.Name record.Insurer
            BusinessField.ClaimedAmount, Admitted.Amount(Money.amount record.Claimed)
            BusinessField.ClaimedCurrency, Admitted.Currency(Money.currencyValue record.Claimed)
        ]

    let replace members value =
        GroupValues.merge members (values value) |> create

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

    let parse input =
        let parsed = RegistrationInput.parsed input
        let errors = GroupValues.errors parsed @ order parsed

        if List.isEmpty errors then
            GroupValues.admitted parsed |> create |> Result.defaultWith invalidOp |> Ok
        else
            Error(List.sortBy fst errors)

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

    let parse input =
        let parsed = DecisionInput.parsed input
        let errors = GroupValues.errors parsed

        if List.isEmpty errors then
            GroupValues.admitted parsed |> create |> Result.defaultWith invalidOp |> Ok
        else
            Error(List.sortBy fst errors)

type PaymentInput = private PaymentInput of GroupValues<Authored>

type Payment = private { Date: CalendarDate }

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

    let create members =
        match members with
        | [ BusinessField.PaymentDate, Admitted.Date date ] -> Ok { Date = date }
        | _ -> Error "Invalid payment members."

    let values (record: Payment) =
        [ BusinessField.PaymentDate, Admitted.Date record.Date ]

    let replace members value =
        GroupValues.merge members (values value) |> create

    let authored value =
        values value
        |> List.map (fun (field, value) -> field, FieldValue.format value)
        |> PaymentInput.create
        |> Result.defaultWith invalidOp

    let parse input =
        let parsed = PaymentInput.parsed input
        let errors = GroupValues.errors parsed

        if List.isEmpty errors then
            GroupValues.admitted parsed |> create |> Result.defaultWith invalidOp |> Ok
        else
            Error(List.sortBy fst errors)

    let onDate date =
        create [ BusinessField.PaymentDate, Admitted.Date date ]
        |> Result.defaultWith invalidOp
