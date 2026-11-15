module ClaimCore.Domain.Tasks.Case.RecordPayment

open ClaimCore.Domain
open ClaimCore.Domain.ResultFlow
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "case.recordPayment"
        ActSubject.Case
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.RecordPayment ]
        (operation
         :: caseTarget
         :: revision
         :: group TaskInputMode.Required RecordGroup.Payment)

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { Payment: Payment }

let private parse admit fields =
    CaseParsing.payment admit fields
    |> Result.map (fun payment -> { Payment = payment })

let check inputs =
    Parsing.check (CaseParsing.values >> parse (fun _ -> Ok)) inputs

let private gate =
    CaseRules.gate true (fun state ->
        state.Case.Value.Content
        |> Option.exists (fun content ->
            content.Finance.Decision
            |> Option.exists (fun decision ->
                Amount.value content.Finance.PaidAmount < Money.value (Decision.payable decision))))

let availability environment state binding =
    let offered =
        CaseRules.availability declaration environment state binding capability gate

    let currency =
        state
        |> Option.bind (fun state -> state.Case.Value.Content)
        |> Option.bind (fun content -> content.Finance.Decision)
        |> Option.map (Decision.payable >> Money.currency)

    { offered with
        Seeds =
            currency
            |> Option.map (fun value ->
                Map.ofList [ field BusinessField.PaidCurrency, InputValue.Text value ])
            |> Option.defaultValue Map.empty
    }

let private failure =
    function
    | FinancialPositionIssue.CurrencyMismatch ->
        Rejection.ofRefusal (
            Refusal.invalidInput (
                InputTarget.Field BusinessField.PaidCurrency,
                Violation.PaymentCurrencyMismatch
            )
        )
    | FinancialPositionIssue.ExceedsPayable ->
        Rejection.ofRefusal (
            Refusal.invalidInput (
                InputTarget.Field BusinessField.PaidAmount,
                Violation.PaymentExceedsPayable
            )
        )
    | FinancialPositionIssue.FuturePayment ->
        Rejection.ofRefusal (
            Refusal.invalidInput (InputTarget.Field BusinessField.PaymentDate, Violation.FutureDate)
        )
    | FinancialPositionIssue.CountOutOfRange ->
        Rejection.ofRefusal (
            Refusal.invalidInput (
                InputTarget.Group RecordGroup.Payment,
                Violation.PaymentCountLimit
            )
        )
    | _ -> Rejection.InvalidState RecordViolation.InvalidFinancialPosition

let decide (context: TaskContext) environment (state: CaseFamily option) binding inputs =
    let fields = CaseParsing.values inputs
    let syntax = parse (fun _ -> Ok) fields

    match state, binding, context.Decision.BusinessDate with
    | Some state, Binding.Case _, Some today ->
        result {
            do!
                CaseRules.admitInputs syntax context environment state binding capability
                |> Parsing.reject

            let before = state.Case.Value.Content

            let admit =
                if context.Decision.Lifetime = DecisionLifetime.Recorded then
                    fun _ -> Ok
                else
                    CaseAdmission.fieldAdmission today None

            let! command =
                Admission.map2
                    (fun command () -> command)
                    (parse admit fields)
                    (CaseParsing.chronology before false false fields)
                |> CaseInput.ordered
                |> Parsing.reject

            let! content = CaseRules.acceptedContent gate state

            do!
                if Map.containsKey context.Stamp.OperationId state.Payments then
                    Error(Rejection.Refused Refusal.IdempotencyConflict)
                else
                    Ok()

            let! finance =
                FinancialPosition.record today command.Payment content.Finance
                |> Result.mapError failure

            let entry = PaymentEntry.recorded context.Stamp.OperationId command.Payment

            return
                [
                    FamilyRules.change
                        context
                        state.Id
                        (OperationId.value context.Stamp.OperationId)
                        Revision.first
                        (PartValue.Payment { Removed = false; Entry = Some entry })
                    CaseRules.change
                        context
                        state
                        { state.Case.Value with
                            Content = Some { content with Finance = finance }
                        }
                ]
        }
        |> Result.map (TaskContext.changes context)
    | _, _, None -> Error(Rejection.InvalidState RecordViolation.InvalidSubjectState)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let task =
    Task.Family
        {
            Declaration = declaration
            Check = check
            Availability = availability
            Decide = decide
            DecisionFactsAvailable = CaseRules.contentAvailable
            Describe = PartDescriptions.family
        }
