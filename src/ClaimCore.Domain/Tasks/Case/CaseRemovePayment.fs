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

let decide context environment (state: DecisionFacts option) binding (_: InputMap) =
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
                    if Revision.value part.Revision = Revision.Maximum then
                        Error(Rejection.InvalidState RecordViolation.InvalidRevisionSequence)
                    else
                        DecisionFacts.remainingPaymentDates command.PaymentId state
                        |> Result.mapError Rejection.InvalidState
                        |> Result.bind (fun bounds ->
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
                                ])))
        |> Result.map (TaskContext.changes context)
    | _ -> Error(Rejection.Refused FamilyRules.unavailable)

let decisionFactsAvailable (state: DecisionFacts option) (_: Binding) =
    state
    |> Option.forall (fun state ->
        state.Case.Value.Content.IsSome
        && (state.Payments
            |> Map.forall (fun _ part -> part.Value.Removed || part.Value.Entry.IsSome)))

let decideRetained context environment (state: DecisionFacts option) binding (_: InputMap) =
    match state, binding with
    | Some state, Binding.CasePayment(_, _, id) ->
        CaseRules.admit context environment state binding capability (FamilyRules.active true)
        |> Parsing.reject
        |> Result.bind (fun () ->
            match Map.tryFind id state.Payments with
            | Some payment when
                not payment.Value.Removed && Revision.value payment.Revision < Revision.Maximum
                ->
                Ok
                    [
                        FamilyRules.change
                            context
                            state.Id
                            (OperationId.value id)
                            (Revision.successor payment.Revision)
                            (PartValue.Payment { Removed = true; Entry = None })
                        CaseRules.change context state { state.Case.Value with Content = None }
                    ]
            | _ -> Error(Rejection.Refused FamilyRules.unavailable))
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
            DecisionFactsAvailable = decisionFactsAvailable
            Describe = PartDescriptions.family
        }
