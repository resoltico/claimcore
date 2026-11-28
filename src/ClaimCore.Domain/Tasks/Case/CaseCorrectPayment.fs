module ClaimCore.Domain.Tasks.Case.CorrectPayment

open ClaimCore.Domain
open ClaimCore.Domain.ResultFlow
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    { family
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
           :: (independentGroup TaskInputMode.Update RecordGroup.Payment
               @ [
                   TaskInputDeclaration.create
                       TaskInputMode.Update
                       (target InputTarget.PaymentDirection)
                   { TaskInputDeclaration.create
                         TaskInputMode.Update
                         (target InputTarget.PaymentReference) with
                       Clearable = true
                   }
                   { TaskInputDeclaration.create
                         TaskInputMode.Update
                         (target InputTarget.PaymentPayee) with
                       Clearable = true
                       RetainedChoiceLabel = Some "label.keepCurrentSelection"
                   }
               ])) with
        Selection =
            Some
                {
                    PromptId = "task.case.correctPayment.selectionPrompt"
                    From = "case.payments"
                }
    }

let private capability = Capability.RecordPayment

let private requiredUpdate input parse inputs =
    Parsing.workValue input parse inputs
    |> Result.bind (function
        | WorkValue.Clear -> Error(Parsing.issue input Violation.TextRequired)
        | WorkValue.Keep -> Ok None
        | WorkValue.Set value -> Ok(Some value))

let private parse inputs =
    let date =
        requiredUpdate (InputTarget.Field BusinessField.PaymentDate) CalendarDate.parse inputs

    let amount =
        requiredUpdate
            (InputTarget.Field BusinessField.PaidAmount)
            (Amount.parse >> Result.bind PaymentAmount.validate)
            inputs

    let currency =
        requiredUpdate (InputTarget.Field BusinessField.PaidCurrency) Currency.parse inputs

    let direction =
        requiredUpdate InputTarget.PaymentDirection RecordPayment.parseDirection inputs

    let reference =
        Parsing.workValue InputTarget.PaymentReference Identifier.parse inputs

    let payee =
        Parsing.workValue InputTarget.PaymentPayee RecordPayment.parsePayee inputs

    Admission.map2
        (fun money details -> money, details)
        (Admission.map2
            (fun date (amount, currency) -> date, amount, currency)
            date
            (Admission.map2 (fun amount currency -> amount, currency) amount currency))
        (Admission.map2
            (fun direction (reference, payee) -> direction, reference, payee)
            direction
            (Admission.map2 (fun reference payee -> reference, payee) reference payee))

let check = Parsing.check parse

let private apply update original =
    match update with
    | WorkValue.Keep -> original
    | WorkValue.Clear -> None
    | WorkValue.Set value -> Some value

let private replace today entry ((date, amount, currency), (direction, reference, payee)) =
    result {
        let previousDate = PaymentEntry.date entry

        let! date =
            match date with
            | Some date when date <> previousDate ->
                CalendarDate.admit today date
                |> Parsing.at (InputTarget.Field BusinessField.PaymentDate)
            | _ -> Ok previousDate

        let! reference =
            match reference with
            | WorkValue.Set value ->
                Identifier.admit value
                |> Parsing.at InputTarget.PaymentReference
                |> Result.map Some
            | _ -> Ok(apply reference (PaymentEntry.reference entry))

        let previousMoney = PaymentEntry.money entry

        let money =
            Money.create
                (defaultArg amount (Money.amount previousMoney))
                (defaultArg currency (Money.currencyValue previousMoney))

        return
            PaymentEntry.create
                (PaymentEntry.id entry)
                (defaultArg direction (PaymentEntry.direction entry))
                money
                date
                reference
                (apply payee (PaymentEntry.payee entry))
            |> Result.defaultWith invalidOp
    }

let private gate =
    CaseRules.gate false (fun state ->
        state.Case.Value.Content
        |> Option.exists (fun content -> content.Finance.PaymentCount > 0L))

let availability environment state binding =
    CaseRules.availability declaration environment state binding capability gate

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
            let syntax = parse inputs

            do!
                CaseRules.admitInputs syntax context environment state binding capability
                |> Parsing.reject

            let! before = CaseRules.acceptedContent gate state

            let! part = selected state id

            let! entry =
                part.Value.Entry
                |> Option.map Ok
                |> Option.defaultValue (
                    Error(Rejection.InvalidState RecordViolation.InvalidPaymentState)
                )

            let! updates = syntax |> Parsing.reject
            let! replacement = replace today entry updates |> Parsing.reject

            do!
                if replacement = entry then
                    Error(Rejection.Refused(Refusal.Case CaseRefusal.CorrectionNoChanges))
                else
                    Ok()

            do!
                if PaymentEntry.payee replacement = PaymentEntry.payee entry then
                    Ok()
                else
                    RecordPayment.admitPayee state (PaymentEntry.payee replacement)

            do!
                if Map.containsKey context.Stamp.OperationId state.Payments then
                    Error(Rejection.Refused Refusal.IdempotencyConflict)
                else
                    Ok()

            let! removed =
                FinancialPosition.remove entry before.Finance
                |> Result.mapError (fun _ ->
                    Rejection.InvalidState RecordViolation.InvalidPaymentState)

            let recorded = PaymentEntry.withId context.Stamp.OperationId replacement

            let! finance =
                FinancialPosition.add recorded removed |> Result.mapError RecordPayment.failure

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
                    (FamilyRules.active false)
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
