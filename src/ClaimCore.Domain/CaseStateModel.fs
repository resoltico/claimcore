namespace ClaimCore.Domain

type Closure =
    {
        Outcome: ClosureOutcome
        Note: NoteText option
    }

[<RequireQualifiedAccess>]
type Handling =
    | Open
    | Closed of Closure

module Handling =
    let status =
        function
        | Handling.Open -> CaseStatus.Opened
        | Handling.Closed _ -> CaseStatus.Closed

    let closure =
        function
        | Handling.Open -> None
        | Handling.Closed closure -> Some closure

    let clearNote =
        function
        | Handling.Open -> Handling.Open
        | Handling.Closed closure -> Handling.Closed { closure with Note = None }

/// Accepted facts retain complete typed groups; a decision is complete or absent.
type CaseFields =
    {
        CaseReference: CaseReference
        Handling: Handling
        Registration: Registration
        Finance: FinancialPosition
    }

    member this.Status = Handling.status this.Handling
    member this.Decision = this.Finance.Decision


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

    let ofLifecycle lifecycle fields =
        match lifecycle with
        | Lifecycle.Active -> CaseState.Active fields
        | Lifecycle.Voided -> CaseState.Voided fields
        | Lifecycle.Erased -> CaseState.Erased

    let token state = lifecycle state |> Lifecycle.token

    /// Work is actionable only on a live, open case.
    let actionable = Lifecycle.Active, CaseStatus.Opened

    let isActionable lifecycle status =
        let requiredLifecycle, requiredStatus = actionable
        lifecycle = requiredLifecycle && status = Some requiredStatus

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
        | BusinessField.Outcome ->
            Handling.closure fields.Handling
            |> Option.map (_.Outcome >> Admitted.Outcome >> CaseFieldValue.Business)
        | BusinessField.ClosingNote ->
            Handling.closure fields.Handling
            |> Option.bind _.Note
            |> Option.map (Admitted.Note >> CaseFieldValue.Business)
        | BusinessField.ReopenReason -> None
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

    let values fields =
        declarations |> List.map (fun row -> row.Field, value row.Field fields)
