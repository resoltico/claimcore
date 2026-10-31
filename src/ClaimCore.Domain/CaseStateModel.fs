namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type PaymentProgress =
    | Undecided
    | Decided of decision: Decision
    | Paid of decision: Decision * payment: Payment

/// Complete groups are retained as typed state; no paid state can omit its decision.
module PaymentProgress =
    let decision =
        function
        | PaymentProgress.Undecided -> None
        | PaymentProgress.Decided value
        | PaymentProgress.Paid(value, _) -> Some value

    let payment =
        function
        | PaymentProgress.Paid(_, date) -> Some date
        | _ -> None

    let paymentDate progress =
        payment progress |> Option.map Payment.date

    let fromGroups decision payment =
        match decision, payment with
        | None, None -> Ok PaymentProgress.Undecided
        | None, Some _ -> Error()
        | Some value, None -> Ok(PaymentProgress.Decided value)
        | Some value, Some payment -> Ok(PaymentProgress.Paid(value, payment))

/// Accepted facts share the authored group types; a decision is complete or absent.
type CaseFields =
    {
        CaseReference: CaseReference
        Status: CaseStatus
        Registration: Registration
        Progress: PaymentProgress
    }

    member this.Decision = PaymentProgress.decision this.Progress

    member this.Payment = PaymentProgress.payment this.Progress

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
        CaseId: CaseId
        Reference: CaseReference
        Revision: Revision
        State: CaseState<CaseFields>
    }

module CaseFields =
    let declarations = BusinessField.declarations
    let ofGroup = BusinessField.ofGroup

    let groupValues registration decision payment =
        Registration.values registration
        @ (decision |> Option.map Decision.values |> Option.defaultValue [])
        @ (payment |> Option.map Payment.values |> Option.defaultValue [])

    let value field (fields: CaseFields) =
        match field with
        | BusinessField.CaseReference -> Some(CaseReference.format fields.CaseReference)
        | BusinessField.Status -> Some(CaseStatus.token fields.Status)
        | _ ->
            groupValues
                fields.Registration
                fields.Decision
                (PaymentProgress.payment fields.Progress)
            |> List.tryFind (fst >> (=) field)
            |> Option.map (snd >> FieldValue.format)

    let values fields =
        declarations |> List.map (fun row -> row.Field, value row.Field fields)
