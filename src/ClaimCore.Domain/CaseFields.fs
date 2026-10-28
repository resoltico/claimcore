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

type RegistrationInput =
    {
        IncidentDate: string
        IncidentNotificationDate: string
        IncidentCountry: string
        ClaimantName: string
        InsurerName: string
        ClaimedAmount: string
        ClaimedCurrency: string
    }

type DecisionInput =
    {
        PaymentDecisionDate: string
        PayableAmount: string
        PayableCurrency: string
    }

type PaymentInput = { PaymentDate: string }

/// Accepted registration values retain their validated structure.
type Registration =
    {
        IncidentDate: CalendarDate
        IncidentNotificationDate: CalendarDate
        IncidentCountry: CountryCode
        ClaimantName: PartyName
        InsurerName: PartyName
        ClaimedAmount: Money
    }

type Decision =
    {
        PaymentDecisionDate: CalendarDate
        PayableAmount: Money
    }

type Payment = { PaymentDate: CalendarDate }

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

[<RequireQualifiedAccess>]
type PaymentProgress =
    | Undecided
    | Decided of decision: Decision
    | Paid of decision: Decision * paidDate: CalendarDate

/// Complete groups are retained as typed state; no paid state can omit its decision.
module PaymentProgress =
    let decision =
        function
        | PaymentProgress.Undecided -> None
        | PaymentProgress.Decided value
        | PaymentProgress.Paid(value, _) -> Some value

    let paymentDate =
        function
        | PaymentProgress.Paid(_, date) -> Some date
        | _ -> None

    let fromGroups decision payment =
        match decision, payment with
        | None, None -> Ok PaymentProgress.Undecided
        | None, Some _ -> Error()
        | Some value, None -> Ok(PaymentProgress.Decided value)
        | Some value, Some date -> Ok(PaymentProgress.Paid(value, date))

/// Accepted facts share the authored group types; a decision is complete or absent.
type CaseFields =
    {
        CaseReference: CaseReference
        Status: CaseStatus
        Registration: Registration
        Progress: PaymentProgress
    }

    member this.Decision = PaymentProgress.decision this.Progress

    member this.Payment =
        PaymentProgress.paymentDate this.Progress
        |> Option.map (fun date -> { PaymentDate = date })

[<RequireQualifiedAccess>]
type Lifecycle =
    | Active
    | Voided
    | Erased

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Lifecycle =
    let vocabulary =
        Vocabulary.ofUnion<Lifecycle> "lifecycle" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

/// Lifecycle is independent of the payload carried by a full view or summary.
[<RequireQualifiedAccess>]
type CaseState<'fields> =
    | Active of 'fields
    | Voided of 'fields
    | Erased

module CaseState =
    let map mapping =
        function
        | CaseState.Active fields -> CaseState.Active(mapping fields)
        | CaseState.Voided fields -> CaseState.Voided(mapping fields)
        | CaseState.Erased -> CaseState.Erased

    let fields =
        function
        | CaseState.Active fields
        | CaseState.Voided fields -> Some fields
        | CaseState.Erased -> None

    let lifecycle =
        function
        | CaseState.Active _ -> Lifecycle.Active
        | CaseState.Voided _ -> Lifecycle.Voided
        | CaseState.Erased -> Lifecycle.Erased

    let token state = lifecycle state |> Lifecycle.token

/// A disclosure cannot carry an erased state.
[<RequireQualifiedAccess>]
type LiveCase =
    | Active of CaseFields
    | Voided of CaseFields

module LiveCase =
    let toState =
        function
        | LiveCase.Active fields -> CaseState.Active fields
        | LiveCase.Voided fields -> CaseState.Voided fields

    let ofState =
        function
        | CaseState.Active fields -> Some(LiveCase.Active fields)
        | CaseState.Voided fields -> Some(LiveCase.Voided fields)
        | CaseState.Erased -> None

type CaseSnapshot =
    {
        Reference: CaseReference
        Revision: Revision
        State: CaseState<CaseFields>
    }

[<RequireQualifiedAccess>]
type FieldValueKind =
    | CalendarDate
    | Country
    | PartyName
    | Amount
    | Currency
    | Reference
    | Status

[<NoEquality; NoComparison>]
type FieldDescriptor =
    {
        Field: BusinessField
        Key: string
        Kind: FieldValueKind
        Group: RecordGroup option
        AllowsAbsence: bool
        IsAuthored: bool
        Value: CaseFields -> string option
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module BusinessField =
    let vocabulary =
        Vocabulary.ofUnion<BusinessField> "field" TokenCasing.CamelCase None

    let key = Vocabulary.token vocabulary

    let private descriptor field kind group allowsAbsence value =
        {
            Field = field
            Key = key field
            Kind = kind
            Group = group
            AllowsAbsence = allowsAbsence
            IsAuthored = field <> BusinessField.Status
            Value = value
        }


    // This exhaustive description owns each field's structure and projection.
    // fsharplint:disable-next-line CyclomaticComplexity
    let describe field =
        match field with
        | BusinessField.CaseReference ->
            descriptor field FieldValueKind.Reference None false (fun fields ->
                Some(CaseReference.format fields.CaseReference))
        | BusinessField.Status ->
            descriptor field FieldValueKind.Status None false (fun fields ->
                Some(CaseStatus.token fields.Status))
        | BusinessField.IncidentDate ->
            descriptor
                field
                FieldValueKind.CalendarDate
                (Some RecordGroup.Registration)
                false
                (fun fields -> Some(CalendarDate.format fields.Registration.IncidentDate))
        | BusinessField.IncidentNotificationDate ->
            descriptor
                field
                FieldValueKind.CalendarDate
                (Some RecordGroup.Registration)
                false
                (fun fields ->
                    Some(CalendarDate.format fields.Registration.IncidentNotificationDate))
        | BusinessField.IncidentCountry ->
            descriptor
                field
                FieldValueKind.Country
                (Some RecordGroup.Registration)
                false
                (fun fields -> Some(CountryCode.format fields.Registration.IncidentCountry))
        | BusinessField.ClaimantName ->
            descriptor
                field
                FieldValueKind.PartyName
                (Some RecordGroup.Registration)
                false
                (fun fields -> Some(PartyName.format fields.Registration.ClaimantName))
        | BusinessField.InsurerName ->
            descriptor
                field
                FieldValueKind.PartyName
                (Some RecordGroup.Registration)
                false
                (fun fields -> Some(PartyName.format fields.Registration.InsurerName))
        | BusinessField.ClaimedAmount ->
            descriptor
                field
                FieldValueKind.Amount
                (Some RecordGroup.Registration)
                false
                (fun fields -> Some(Money.format fields.Registration.ClaimedAmount))
        | BusinessField.ClaimedCurrency ->
            descriptor
                field
                FieldValueKind.Currency
                (Some RecordGroup.Registration)
                false
                (fun fields -> Some(Money.currency fields.Registration.ClaimedAmount))
        | BusinessField.PaymentDecisionDate ->
            descriptor
                field
                FieldValueKind.CalendarDate
                (Some RecordGroup.Decision)
                true
                (fun fields ->
                    fields.Decision
                    |> Option.map (fun value -> CalendarDate.format value.PaymentDecisionDate))
        | BusinessField.PayableAmount ->
            descriptor field FieldValueKind.Amount (Some RecordGroup.Decision) true (fun fields ->
                fields.Decision |> Option.map (fun value -> Money.format value.PayableAmount))
        | BusinessField.PayableCurrency ->
            descriptor field FieldValueKind.Currency (Some RecordGroup.Decision) true (fun fields ->
                fields.Decision |> Option.map (fun value -> Money.currency value.PayableAmount))
        | BusinessField.PaymentDate ->
            descriptor
                field
                FieldValueKind.CalendarDate
                (Some RecordGroup.Payment)
                true
                (fun fields ->
                    fields.Payment
                    |> Option.map (fun value -> CalendarDate.format value.PaymentDate))

    let values fields =
        Vocabulary.all vocabulary
        |> List.map (fun field -> field, (describe field).Value fields)

/// Domain declarations carry structure; each boundary chooses its own names and codecs.
module CaseFields =
    let declarations =
        Vocabulary.all BusinessField.vocabulary |> List.map BusinessField.describe

    let ofGroup group =
        declarations
        |> List.filter (fun row -> row.Group = Some group)
        |> List.map _.Field
