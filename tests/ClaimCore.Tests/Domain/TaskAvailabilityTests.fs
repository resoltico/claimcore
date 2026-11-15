module ClaimCore.Tests.TaskAvailabilityTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Tasks
open ClaimCore.Tests.TaskBuilders

let private binding (declaration: TaskDeclaration) (state: CaseFamily) =
    if declaration.Creates then
        Binding.CaseNew Revision.zero
    elif declaration.ReviewsFamilySequence then
        Binding.CaseErase(target state, state.Case.Revision, state.Sequence)
    elif
        declaration.Inputs
        |> List.exists (fun input -> input.Input = TaskInputId.Target InputTarget.PaymentId)
    then
        let payment =
            state.Payments
            |> Map.toList
            |> List.tryFind (fun (_, part) -> not part.Value.Removed)
            |> Option.map fst
            |> Option.defaultValue (operation 999)

        Binding.CasePayment(target state, state.Case.Revision, payment)
    else
        caseBinding state

let private offered environment state =
    Registry.all
    |> List.choose (function
        | Task.Family task when task.Declaration.Target = ActSubject.Case ->
            if
                (task.Availability environment (Some state) (binding task.Declaration state))
                    .Blocked.IsNone
            then
                Some(TaskId.token task.Declaration.Id)
            else
                None
        | _ -> None)
    |> Set.ofList

[<Tests>]
let tests =
    testList
        "task availability across case states"
        [
            testCase
                "the independent financial and lifecycle matrix preserves every offered case task"
                (fun () ->
                    let zeroBase = family ()

                    let zero =
                        ClaimCore.Domain.Tasks.Case.Decide.decide
                            (context 2)
                            environment
                            (Some zeroBase)
                            (caseBinding zeroBase)
                            (decisionInputs "2026-02-01" "0" "EUR")
                        |> accepted
                        |> apply zeroBase

                    let common = [ "case.close"; "case.void"; "case.erase" ]

                    let rows =
                        [
                            family (), [ "case.amendRegistration"; "case.decide" ]
                            decided (),
                            [
                                "case.correct"
                                "case.decide"
                                "case.withdrawDecision"
                                "case.recordPayment"
                            ]
                            decided () |> pay 3 "2026-02-02" "25",
                            [
                                "case.correct"
                                "case.decide"
                                "case.recordPayment"
                                "case.removePayment"
                            ]
                            decided () |> pay 3 "2026-02-02" "1000",
                            [ "case.correct"; "case.decide"; "case.removePayment" ]
                            zero, [ "case.correct"; "case.decide"; "case.withdrawDecision" ]
                        ]

                    for state, financial in rows do
                        Expect.equal
                            (offered environment state)
                            (Set.ofList (common @ financial))
                            "Open-case offers follow accepted entry state"

                        let closed = state |> setCase Lifecycle.Active CaseStatus.Closed

                        Expect.equal
                            (offered environment closed)
                            (Set.ofList
                                [ "case.correct"; "case.reopen"; "case.void"; "case.erase" ])
                            "Closed-case offers"

                        let voided = state |> setCase Lifecycle.Voided state.Case.Value.Status

                        Expect.equal
                            (offered environment voided)
                            (Set.ofList [ "case.reinstate"; "case.erase" ])
                            "Voided-case offers"

                        let erased = state |> setCase Lifecycle.Erased state.Case.Value.Status
                        Expect.isEmpty (offered environment erased) "Erasure is terminal")
            testCase "current requester permissions and maintenance suppress offers" (fun () ->
                let state = decided ()

                let reader =
                    AccessView.ofActor (
                        Some
                            {
                                Actor = actor
                                Grants =
                                    [
                                        {
                                            Role = Role.CaseReader
                                            Scope = Scope.Installation
                                        }
                                    ]
                            }
                    )

                Expect.isEmpty
                    (offered { environment with Requester = reader } state)
                    "Read permission does not grant a mutation"

                Expect.isEmpty
                    (offered { environment with Maintenance = true } state)
                    "The current maintenance state is authoritative")
        ]
