module ClaimCore.Domain.Tasks.Case.RemovePayment

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Inputs
open ClaimCore.Domain.Tasks
open ClaimCore.Domain.Tasks.Declarations

let declaration =
    family
        "case.removePayment"
        ActSubject.Case
        false
        ActFamily.Business
        Consequence.Routine
        [ Capability.RecordPayment ]
        [
            operation
            caseTarget
            revision
            required (target InputTarget.PaymentId) |> bound
        ]

let private capability = List.exactlyOne declaration.Capabilities

type private Command = { PaymentId: OperationId }
let check (_: InputMap) = []

let private gate =
    CaseRules.gate true (fun state ->
        state.Case.Value.Content
        |> Option.exists (fun content ->
            content.Finance.Decision.IsSome && content.Finance.PaymentCount > 0L))

let availability environment state binding =
    CaseRules.availability declaration environment state binding capability gate

let decide context environment (state: CaseFamily option) binding (_: InputMap) =
    match state, binding with
    | Some state, Binding.CasePayment(_, _, id) ->
        let command = { PaymentId = id }

        CaseRules.admit context environment state binding capability gate
        |> Parsing.reject
        |> Result.bind (fun () -> CaseRules.content state)
        |> Result.bind (fun before ->
            match Map.tryFind command.PaymentId state.Payments with
            | None -> Error(Rejection.Refused(Refusal.Case CaseRefusal.PaymentNotFound))
            | Some part when part.Value.Removed ->
                Error(Rejection.Refused(Refusal.Case CaseRefusal.PaymentNotFound))
            | Some part ->
                match part.Value.Entry with
                | None -> Error(Rejection.InvalidState RecordViolation.InvalidPaymentState)
                | Some entry ->
                    let others =
                        state.Payments
                        |> Map.remove command.PaymentId
                        |> Map.values
                        |> Seq.filter (fun part -> not part.Value.Removed)
                        |> Seq.toList

                    if others |> List.exists (fun part -> part.Value.Entry.IsNone) then
                        Error(Rejection.InvalidState RecordViolation.InvalidPaymentState)
                    elif Revision.value part.Revision = Revision.Maximum then
                        Error(Rejection.InvalidState RecordViolation.InvalidRevisionSequence)
                    else
                        let dates =
                            others
                            |> List.choose _.Value.Entry
                            |> List.map PaymentEntry.date
                            |> List.sort

                        let bounds =
                            match dates with
                            | [] -> None
                            | earliest :: _ -> PaymentDateBounds.create earliest (List.last dates)

                        FinancialPosition.remove entry bounds before.Finance
                        |> Result.mapError (fun _ ->
                            Rejection.InvalidState RecordViolation.InvalidPaymentState)
                        |> Result.map (fun finance ->
                            [
                                FamilyRules.change
                                    context
                                    state.Id
                                    (OperationId.value id)
                                    (Revision.successor part.Revision)
                                    (PartValue.Payment { Removed = true; Entry = Some entry })
                                CaseRules.change
                                    context
                                    state
                                    { state.Case.Value with
                                        Content = Some { before with Finance = finance }
                                    }
                            ]))
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decisionFactsAvailable (state: CaseFamily option) (_: Binding) =
    state
    |> Option.forall (fun state ->
        state.Case.Value.Content.IsSome
        && (state.Payments
            |> Map.forall (fun _ part -> part.Value.Removed || part.Value.Entry.IsSome)))

let task =
    Task.Family
        {
            Declaration = declaration
            Check = check
            Availability = availability
            Decide = decide
            DecisionFactsAvailable = decisionFactsAvailable
            Describe = PartDescriptions.family
        }
