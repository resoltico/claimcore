namespace ClaimCore.Domain

type PaymentTotals =
    {
        Payable: Money
        Paid: Money
        Remaining: Money
    }

[<RequireQualifiedAccess>]
type PaymentProgress =
    | Undecided
    | Decided of decision: Decision
    | PartlyPaid of decision: Decision * payments: PaymentEntry list
    | Paid of decision: Decision * payments: PaymentEntry list

module PaymentProgress =
    [<Literal>]
    let MaximumPayments = 50

    let decision =
        function
        | PaymentProgress.Undecided -> None
        | PaymentProgress.Decided value
        | PaymentProgress.PartlyPaid(value, _)
        | PaymentProgress.Paid(value, _) -> Some value

    let payments =
        function
        | PaymentProgress.PartlyPaid(_, entries)
        | PaymentProgress.Paid(_, entries) -> entries
        | _ -> []

    let paidValue progress =
        payments progress |> List.sumBy (PaymentEntry.amount >> Amount.value)

    let lastPaymentDate progress =
        payments progress
        |> List.map PaymentEntry.date
        |> List.sortDescending
        |> List.tryHead

    let fromGroups decision (entries: PaymentEntry list) =
        if
            entries.Length > MaximumPayments
            || (entries |> List.map PaymentEntry.id |> Set.ofList |> Set.count)
               <> entries.Length
        then
            Error()
        else
            match decision, entries with
            | None, [] -> Ok PaymentProgress.Undecided
            | None, _ -> Error()
            | Some value, entries ->
                let total = entries |> List.sumBy (PaymentEntry.amount >> Amount.value)
                let payable = Money.value (Decision.payable value)

                if total > payable then
                    Error()
                elif total = payable then
                    Ok(PaymentProgress.Paid(value, entries))
                elif total = 0M then
                    Ok(PaymentProgress.Decided value)
                else
                    Ok(PaymentProgress.PartlyPaid(value, entries))

    let private amount (value: decimal) =
        Amount.parse (value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
        |> Result.defaultWith (fun _ ->
            invalidOp "A validated payment total fits its payable amount.")

    let totals progress =
        decision progress
        |> Option.map (fun decision ->
            let payable = Decision.payable decision
            let paid = paidValue progress
            let currency = Money.currencyValue payable

            {
                Payable = payable
                Paid = Money.create (amount paid) currency
                Remaining = Money.create (amount (Money.value payable - paid)) currency
            })

    let paid progress = totals progress |> Option.map _.Paid

    let remaining progress =
        totals progress |> Option.map _.Remaining

/// Accepted facts retain complete typed groups; a decision is complete or absent.
type CaseFields =
    {
        CaseReference: CaseReference
        Status: CaseStatus
        Registration: Registration
        Progress: PaymentProgress
    }

    member this.Decision = PaymentProgress.decision this.Progress

    member this.Payments = PaymentProgress.payments this.Progress

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

    /// Retained state structure is independent of current permissions and scalar admission.
    let after state act =
        match state, act with
        | Lifecycle.Erased, _ -> None
        | Lifecycle.Active, CaseActKind.Void -> Some Lifecycle.Voided
        | Lifecycle.Voided, CaseActKind.Reinstate -> Some Lifecycle.Active
        | Lifecycle.Voided, CaseActKind.Void
        | Lifecycle.Active, CaseActKind.Reinstate -> None
        | _, CaseActKind.Erase -> Some Lifecycle.Erased
        | Lifecycle.Voided, _ -> None
        | Lifecycle.Active, _ -> Some Lifecycle.Active

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

    let ofLifecycle lifecycle fields =
        match lifecycle with
        | Lifecycle.Active -> CaseState.Active fields
        | Lifecycle.Voided -> CaseState.Voided fields
        | Lifecycle.Erased -> CaseState.Erased

    let token state = lifecycle state |> Lifecycle.token

    /// Work is actionable only on a live, open case.
    let isActionable lifecycle status =
        lifecycle = Lifecycle.Active && status = Some CaseStatus.Opened

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
        CaseId: CaseId
        Reference: CaseReference
        Revision: Revision
        State: CaseState<CaseFields>
    }

[<RequireQualifiedAccess>]
type CaseFieldValue =
    | Business of Admitted
    | Reference of CaseReference
    | Status of CaseStatus

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseFieldValue =
    let format =
        function
        | CaseFieldValue.Business value -> FieldValue.format value
        | CaseFieldValue.Reference value -> CaseReference.format value
        | CaseFieldValue.Status value -> CaseStatus.token value

module CaseFields =
    let declarations = BusinessField.declarations
    let ofGroup = BusinessField.ofGroup

    let groupValues registration decision =
        Registration.values registration
        @ (decision |> Option.map Decision.values |> Option.defaultValue [])

    let typedValue field (fields: CaseFields) =
        match field with
        | BusinessField.CaseReference -> Some(CaseFieldValue.Reference fields.CaseReference)
        | BusinessField.Status -> Some(CaseFieldValue.Status fields.Status)
        | BusinessField.PaidAmount
        | BusinessField.PaidCurrency
        | BusinessField.PaymentDate -> None
        | BusinessField.ResponsibleActor
        | BusinessField.HandlingSummary -> None
        | _ ->
            groupValues fields.Registration fields.Decision
            |> List.tryFind (fst >> (=) field)
            |> Option.map (snd >> CaseFieldValue.Business)

    let value field fields =
        typedValue field fields |> Option.map CaseFieldValue.format

    let listedValues (declarations: FieldDescriptor list) fields =
        declarations
        |> List.filter _.Listed
        |> List.map (fun declaration -> declaration.Field, typedValue declaration.Field fields)
        |> Map.ofList

    let values fields =
        declarations |> List.map (fun row -> row.Field, value row.Field fields)
