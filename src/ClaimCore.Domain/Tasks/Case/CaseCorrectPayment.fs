module ClaimCore.Domain.Tasks.Case.CorrectPayment

open ClaimCore.Domain
open ClaimCore.Domain.ResultFlow
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "case.correctPayment"
        ActSubject.Case
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.RecordPayment ]
        (operation
         :: caseTarget
         :: revision
         :: (required (target InputTarget.PaymentId) |> bound)
         :: group TaskInputMode.Required RecordGroup.Payment)

let private capability = Capability.RecordPayment
let check = RecordPayment.check

let private gate =
    CaseRules.gate true (fun state ->
        state.Case.Value.Content
        |> Option.exists (fun value ->
            value.Finance.Decision.IsSome && value.Finance.PaymentCount > 0L))

let availability environment state binding =
    let offered =
        CaseRules.availability declaration environment state binding capability gate

    let currency =
        state
        |> Option.bind (fun state -> state.Case.Value.Content)
        |> Option.bind (fun value -> value.Finance.Decision)
        |> Option.map (Decision.payable >> Money.currency)

    { offered with
        Seeds =
            currency
            |> Option.map (fun value ->
                Map.ofList [ field BusinessField.PaidCurrency, InputValue.Text value ])
            |> Option.defaultValue Map.empty
    }

let private selected (state: DecisionFacts) id =
    match Map.tryFind id state.Payments with
    | Some part when not part.Value.Removed && Revision.value part.Revision < Revision.Maximum ->
        Ok part
    | Some part when not part.Value.Removed ->
        Error(Rejection.InvalidState RecordViolation.InvalidRevisionSequence)
    | _ -> Error(Rejection.Refused(Refusal.Case CaseRefusal.PaymentNotFound))

let decide context environment (state: DecisionFacts option) binding inputs =
    match state, binding, context.Decision.BusinessDate with
    | Some state, Binding.CasePayment(_, _, id), Some today ->
        result {
            let fields = CaseParsing.values inputs
            let syntax = CaseParsing.payment (fun _ -> Ok) fields

            do!
                CaseRules.admitInputs syntax context environment state binding capability
                |> Parsing.reject

            let! before = CaseRules.acceptedContent gate state

            let! replacement =
                CaseParsing.payment (CaseAdmission.fieldAdmission today None) fields
                |> Parsing.reject

            let! part = selected state id

            let! entry =
                part.Value.Entry
                |> Option.map Ok
                |> Option.defaultValue (
                    Error(Rejection.InvalidState RecordViolation.InvalidPaymentState)
                )

            do!
                if Map.containsKey context.Stamp.OperationId state.Payments then
                    Error(Rejection.Refused Refusal.IdempotencyConflict)
                else
                    Ok()

            let! bounds =
                DecisionFacts.remainingPaymentDates id state
                |> Result.mapError Rejection.InvalidState

            let! removed =
                FinancialPosition.remove entry bounds before.Finance
                |> Result.mapError (fun _ ->
                    Rejection.InvalidState RecordViolation.InvalidPaymentState)

            let! finance =
                FinancialPosition.record today replacement removed
                |> Result.mapError RecordPayment.failure

            let recorded = PaymentEntry.recorded context.Stamp.OperationId replacement

            return
                [
                    FamilyRules.change
                        context
                        state.Id
                        (OperationId.value id)
                        (Revision.successor part.Revision)
                        (PartValue.Payment { Removed = true; Entry = Some entry })
                    FamilyRules.change
                        context
                        state.Id
                        (OperationId.value context.Stamp.OperationId)
                        Revision.first
                        (PartValue.Payment
                            {
                                Removed = false
                                Entry = Some recorded
                            })
                    CaseRules.change
                        context
                        state
                        { state.Case.Value with
                            Content = Some { before with Finance = finance }
                        }
                ]
        }
        |> Result.map (TaskContext.changes context)
    | _, _, None -> Error(Rejection.InvalidState RecordViolation.InvalidSubjectState)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decideRetained context environment (state: DecisionFacts option) binding (_: InputMap) =
    match state, binding with
    | Some state, Binding.CasePayment(_, _, id) ->
        result {
            do!
                CaseRules.admit
                    context
                    environment
                    state
                    binding
                    capability
                    (FamilyRules.active true)
                |> Parsing.reject

            let! part = selected state id

            return
                [
                    FamilyRules.change
                        context
                        state.Id
                        (OperationId.value id)
                        (Revision.successor part.Revision)
                        (PartValue.Payment { Removed = true; Entry = None })
                    FamilyRules.change
                        context
                        state.Id
                        (OperationId.value context.Stamp.OperationId)
                        Revision.first
                        (PartValue.Payment { Removed = false; Entry = None })
                    CaseRules.change context state { state.Case.Value with Content = None }
                ]
        }
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let task =
    Task.Family
        {
            Declaration = declaration
            Authorize =
                fun principal access caseId _ ->
                    match caseId with
                    | Some id ->
                        FamilyRules.permissionFor principal access (Scope.Case id) capability
                    | None -> Error FamilyRules.unavailable
            Check = check
            Availability = availability
            Decide = decide
            DecideRetained = decideRetained
            DecisionFactsAvailable = RemovePayment.decisionFactsAvailable
            Describe = PartDescriptions.family
        }
