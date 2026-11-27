module ClaimCore.Tests.CaseBuilders

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Tasks
open ClaimCore.TestSupport

let today = DateOnly(2026, 9, 7)

let registration = DomainInputs.exampleRegistration

let decision = DomainInputs.decision

let accepted result =
    match result with
    | Ok value -> value
    | Error error -> failtestf "Expected acceptance: %A" error

let opened () =
    let original = TaskBuilders.initialChange

    let id =
        Guid.Parse "20000000-0000-7000-8000-000000000001"
        |> OperationId.create
        |> Option.get

    let reference =
        ClaimCore.TestSupport.DomainInputs.reference "CC-ZQ16-FT6R-54T8-E39K-67"

    let content =
        {
            CaseContent.Registration = DomainInputs.admittedRegistration registration
            Finance = FinancialPosition.undecided
        }

    let change =
        { original with
            Aggregate = AggregateReference.Family(CaseId.ofOperationId id)
            PartId = OperationId.value id
            NaturalKey = Some(CaseReference.format reference)
            Stamp = { original.Stamp with OperationId = id }
            Value =
                PartValue.Case
                    {
                        Reference = Some reference
                        Lifecycle = Lifecycle.Active
                        Handling = Handling.Open
                        Content = Some content
                        Disposition = None
                        ReopenReason = None
                    }
        }

    CaseFamily.apply None change |> accepted

let familySnapshot family =
    CaseFamily.snapshot family |> Option.get

let apply task inputs (family: CaseFamily) =
    let number = int (Sequence.value family.Sequence) + 1
    let context = TaskBuilders.context number

    let context =
        { context with
            Decision =
                { context.Decision with
                    BusinessDate = Some today
                    Lifetime = DecisionLifetime.Execution None
                }
        }

    match Registry.find (TaskId.ofLiteral task) with
    | Some(Task.Family task) ->
        task.Decide
            context
            TaskBuilders.environment
            (Some(DecisionFacts.fromReplay family))
            (Binding.Case(CaseSelector.Id family.Id, family.Case.Revision))
            inputs
        |> Result.map (TaskBuilders.apply family)
    | _ -> failtest "Expected a family task."

let decided () =
    opened ()
    |> apply "case.decide" (TaskBuilders.decisionInputs "2026-08-15" "750.00" "EUR")
    |> accepted

let paid () =
    let family = decided ()

    let context =
        { TaskBuilders.context 3 with
            Decision =
                { (TaskBuilders.context 3).Decision with
                    Lifetime = DecisionLifetime.Execution None
                    BusinessDate = Some today
                }
        }

    let inputs = TaskBuilders.paymentInputs "2026-08-20" "750.00" "EUR"

    match Registry.find (TaskId.ofLiteral "case.recordPayment") with
    | Some(Task.Family task) ->
        task.Decide
            context
            TaskBuilders.environment
            (Some(DecisionFacts.fromReplay family))
            (Binding.Case(CaseSelector.Id family.Id, family.Case.Revision))
            inputs
        |> accepted
        |> TaskBuilders.apply family
    | _ -> failtest "Expected the payment task."

let viewFields (snapshot: CaseSnapshot) =
    match snapshot.State with
    | (CaseState.Active(fields) | CaseState.Voided(fields)) -> fields
    | CaseState.Erased -> failtest "Expected retained business fields"

let snapshot revision (fields: CaseFields) =
    {
        CaseId = CaseFixtures.caseId (System.Guid.Parse "20000000-0000-7000-8000-000000000001")
        Reference = fields.CaseReference
        Revision = CaseFixtures.revision revision
        State = CaseState.Active(fields)
    }
