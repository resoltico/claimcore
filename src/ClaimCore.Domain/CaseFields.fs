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
    | ReceivedDate
    | IncidentCountry
    | IncidentDescription
    | PolicyNumber
    | InsurerClaimReference
    | ClaimedAmount
    | ClaimedCurrency
    | CaseReference
    | PaymentDecisionDate
    | PayableAmount
    | PayableCurrency
    | DecisionBasis
    | Outcome
    | ClosingNote
    | ReopenReason
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
    | ExpectedFamilySequence
    | Group of RecordGroup
    | Reason
    | PrincipalKind
    | PrincipalIssuer
    | PrincipalSubject
    | ProfileDisplayName
    | ProfileContact
    | ProfileDigestEmail
    | DirectorySearch
    | Grants
    | Role
    | Scope
    | CaseId
    | Limit
    | Cursor
    | Text
    | ReminderId
    | NoteId
    | AttachmentId
    | AttachmentName
    | AttachmentLength
    | AttachmentDigest
    | LinkId
    | LinkTargetReference
    | LinkTargetId
    | LinkNote
    | NoteText
    | Parties
    | PartyId
    | PartyRole
    | PartyName
    | PartyDetails
    | ExpectedPartyRevision
    | PaymentId
    | PaymentDirection
    | PaymentReference
    | PaymentPayee
    | ReminderAction
    | ReminderDueDate
    | ReminderPlanKind
    | ReminderDoneOn
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
            AllowsAbsence = group = Some RecordGroup.Registration
            IsAuthored =
                match field with
                | BusinessField.Status
                | BusinessField.ReopenReason -> false
                | _ -> true
            Pairs = pairs
        }
    // Domain owns value structure and amount/currency relationships.
    let describe field =
        match field with
        | BusinessField.IncidentDate ->
            descriptor field FieldValueKind.CalendarDate (Some RecordGroup.Registration) None
        | BusinessField.ReceivedDate ->
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
        | BusinessField.DecisionBasis ->
            { descriptor field FieldValueKind.NoteText (Some RecordGroup.Decision) None with
                AllowsAbsence = true
            }
        | BusinessField.Outcome -> descriptor field FieldValueKind.ClosureOutcome None None
        | BusinessField.ClosingNote ->
            { descriptor field FieldValueKind.NoteText None None with
                AllowsAbsence = true
            }
        | BusinessField.ReopenReason -> descriptor field FieldValueKind.Reason None None
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
        elif
            supplied
            |> List.exists (fun field ->
                (BusinessField.describe field).Pairs
                |> Option.exists (fun pair -> not (List.contains pair supplied)))
            || (expected
                |> Set.exists (fun field ->
                    (BusinessField.describe field).Pairs
                    |> Option.exists (fun pair ->
                        List.contains pair supplied && not (List.contains field supplied))))
        then
            Error "An amount and its currency must be supplied together."
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

    let private optional field (Registration values) = GroupValues.tryValue field values

    let incidentDate record =
        optional BusinessField.IncidentDate record |> Option.map FieldValue.date

    let receivedDate record =
        optional BusinessField.ReceivedDate record |> Option.map FieldValue.date

    let country record =
        optional BusinessField.IncidentCountry record |> Option.map FieldValue.country

    let claimed record =
        Option.map2
            Money.create
            (optional BusinessField.ClaimedAmount record |> Option.map FieldValue.amount)
            (optional BusinessField.ClaimedCurrency record |> Option.map FieldValue.currency)

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
                Ok(Registration values))

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

    let internal parsePreparedWith admit parsed =
        let admitted = GroupValues.check admit parsed

        let supplied field =
            admitted |> List.exists (fst >> (=) field)

        let paired =
            match supplied BusinessField.ClaimedAmount, supplied BusinessField.ClaimedCurrency with
            | true, false -> [ BusinessField.ClaimedCurrency, Violation.TextRequired ]
            | false, true -> [ BusinessField.ClaimedAmount, Violation.TextRequired ]
            | _ -> []

        let errors = GroupValues.errors admitted @ paired

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
            Basis: NoteText option
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
    let basis (record: Decision) = record.Basis

    let create members =
        let basis, required =
            members |> List.partition (fst >> (=) BusinessField.DecisionBasis)

        match List.sortBy fst required, basis with
        | [ BusinessField.PaymentDecisionDate, Admitted.Date date
            BusinessField.PayableAmount, Admitted.Amount amount
            BusinessField.PayableCurrency, Admitted.Currency currency ],
          ([] | [ BusinessField.DecisionBasis, Admitted.Note _ ]) ->
            Ok
                {
                    Date = date
                    Payable = Money.create amount currency
                    Basis = basis |> List.tryHead |> Option.map (snd >> FieldValue.note)
                }
        | _ -> Error "Invalid decision members."

    let clearBasis decision = { decision with Basis = None }

    let values (record: Decision) =
        [
            BusinessField.PaymentDecisionDate, Admitted.Date record.Date
            BusinessField.PayableAmount, Admitted.Amount(Money.amount record.Payable)
            BusinessField.PayableCurrency, Admitted.Currency(Money.currencyValue record.Payable)
            match record.Basis with
            | Some basis -> BusinessField.DecisionBasis, Admitted.Note basis
            | None -> ()
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

[<RequireQualifiedAccess>]
type PaymentDirection =
    | Paid
    | Returned

module PaymentDirection =
    let vocabulary =
        Vocabulary.ofUnion<PaymentDirection> "paymentDirection" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary
    let defaultValue = PaymentDirection.Paid

type Payment =
    private
        {
            Date: CalendarDate
            Money: Money
            Direction: PaymentDirection
            Reference: Identifier option
            Payee: PartyId option
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
    let money (record: Payment) = record.Money
    let direction (record: Payment) = record.Direction
    let reference (record: Payment) = record.Reference
    let payee (record: Payment) = record.Payee

    let withDetails direction reference payee payment =
        { payment with
            Direction = direction
            Reference = reference
            Payee = payee
        }

    let create members =
        match List.sortBy fst members with
        | [ BusinessField.PaymentDate, Admitted.Date date
            BusinessField.PaidAmount, Admitted.Amount amount
            BusinessField.PaidCurrency, Admitted.Currency currency ] ->
            PaymentAmount.validate amount
            |> Result.map (fun amount ->
                {
                    Date = date
                    Money = Money.create amount currency
                    Direction = PaymentDirection.defaultValue
                    Reference = None
                    Payee = None
                })
            |> Result.mapError (fun _ -> "A payment amount must be positive.")
        | _ -> Error "Invalid payment members."

    let values (record: Payment) =
        [
            BusinessField.PaymentDate, Admitted.Date record.Date
            BusinessField.PaidAmount, Admitted.Amount(Money.amount record.Money)
            BusinessField.PaidCurrency, Admitted.Currency(Money.currencyValue record.Money)
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

/// The accepting operation identifies an entry; its money and evidence belong to that entry.
type PaymentEntry =
    private
        {
            Id: OperationId
            Date: CalendarDate
            Money: Money
            Direction: PaymentDirection
            Reference: Identifier option
            Payee: PartyId option
        }

module PaymentEntry =
    let internal withId id (entry: PaymentEntry) = { entry with Id = id }
    let id (entry: PaymentEntry) = entry.Id
    let date (entry: PaymentEntry) = entry.Date
    let money (entry: PaymentEntry) = entry.Money
    let amount entry = money entry |> Money.amount
    let direction (entry: PaymentEntry) = entry.Direction
    let reference (entry: PaymentEntry) = entry.Reference
    let payee (entry: PaymentEntry) = entry.Payee

    let create id direction money date reference payee =
        PaymentAmount.validate (Money.amount money)
        |> Result.map (fun _ ->
            {
                Id = id
                Date = date
                Money = money
                Direction = direction
                Reference = reference
                Payee = payee
            })
        |> Result.mapError (fun _ -> "A payment entry amount must be positive.")

    let recorded id payment =
        {
            Id = id
            Date = Payment.date payment
            Money = Payment.money payment
            Direction = Payment.direction payment
            Reference = Payment.reference payment
            Payee = Payment.payee payment
        }
