namespace ClaimCore.Domain


[<RequireQualifiedAccess>]
type RecordGroup =
    | Registration
    | Decision
    | Payment

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

    let declarations = Vocabulary.all vocabulary |> List.map describe

    let ofGroup group =
        declarations
        |> List.filter (fun field -> field.Group = Some group)
        |> List.map _.Field

type GroupValues<'stage> = private GroupValues of RecordGroup * Map<BusinessField, 'stage>

module GroupValues =
    let private create group members =
        let expected = BusinessField.ofGroup group
        let supplied = members |> List.map fst

        if
            List.length supplied <> List.length expected
            || Set.ofList supplied <> Set.ofList expected
        then
            Error "A group must contain each declared field exactly once."
        else
            Ok(GroupValues(group, Map.ofList members))

    let items (GroupValues(group, values)) =
        BusinessField.ofGroup group |> List.map (fun field -> field, values[field])

    let get field (GroupValues(_, values)) = values[field]
    let raw field values = get field values |> Authored.format

    let money field values =
        let currency =
            (BusinessField.describe field).Pairs
            |> Option.defaultWith (fun () -> invalidOp "An amount must declare its currency pair.")

        Money.create
            (get field values |> FieldValue.amount)
            (get currency values |> FieldValue.currency)

    let createAuthored group members =
        members
        |> List.map (fun (field, value) -> field, Authored.create value)
        |> create group

    let createAdmitted group members =
        if
            members
            |> List.exists (fun (field, value) ->
                FieldValue.kind value <> (BusinessField.describe field).Kind)
        then
            Error "A field's value must have its declared kind."
        else
            create group members

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

type Registration = private Registration of GroupValues<Admitted>

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
    let value field (Registration values) = GroupValues.get field values

    let incidentDate record =
        value BusinessField.IncidentDate record |> FieldValue.date

    let incidentNotificationDate record =
        value BusinessField.IncidentNotificationDate record |> FieldValue.date

    let claimed (Registration values) =
        GroupValues.money BusinessField.ClaimedAmount values


    let create members =
        GroupValues.createAdmitted RecordGroup.Registration members
        |> Result.map Registration

    let values (Registration values) = GroupValues.items values

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

type Decision = private Decision of GroupValues<Admitted>

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
    let value field (Decision values) = GroupValues.get field values

    let date record =
        value BusinessField.PaymentDecisionDate record |> FieldValue.date

    let payable (Decision values) =
        GroupValues.money BusinessField.PayableAmount values


    let create members =
        GroupValues.createAdmitted RecordGroup.Decision members |> Result.map Decision

    let values (Decision values) = GroupValues.items values

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

type Payment = private Payment of GroupValues<Admitted>

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
    let value field (Payment values) = GroupValues.get field values

    let date record =
        value BusinessField.PaymentDate record |> FieldValue.date


    let create members =
        GroupValues.createAdmitted RecordGroup.Payment members |> Result.map Payment

    let values (Payment values) = GroupValues.items values

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
