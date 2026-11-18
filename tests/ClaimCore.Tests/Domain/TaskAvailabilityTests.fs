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
                (task.Availability
                    environment
                    (Some(DecisionFacts.fromReplay state))
                    (binding task.Declaration state))
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
                "reminder acquisition seeds contain only each task's authored inputs"
                (fun () ->
                    let state, id = reminder ()
                    let binding = reminderBinding state id Revision.first

                    for available in
                        [
                            ClaimCore.Domain.Tasks.Reminder.Complete.availability
                            ClaimCore.Domain.Tasks.Reminder.Cancel.availability
                        ] do
                        let offered =
                            available environment (Some(DecisionFacts.fromReplay state)) binding

                        Expect.isNone offered.Blocked "Terminal work is available"

                        Expect.isEmpty
                            offered.Seeds
                            "Complete and cancel accept no authored values"

                    let plan =
                        Map.ofList
                            [
                                TaskInputId.Target InputTarget.ReminderAction,
                                InputValue.Text "Request photographs"
                                TaskInputId.Target InputTarget.ReminderDueDate,
                                InputValue.Text "2026-11-16"
                            ]

                    for available in
                        [
                            ClaimCore.Domain.Tasks.Reminder.Update.availability
                            ClaimCore.Domain.Tasks.Reminder.CompleteAndPlanNext.availability
                        ] do
                        Expect.equal
                            (available environment (Some(DecisionFacts.fromReplay state)) binding)
                                .Seeds
                            plan
                            "Planning tasks acquire both current plan values"

                    let completed =
                        ClaimCore.Domain.Tasks.Reminder.Complete.decide
                            (context 3)
                            environment
                            (Some(DecisionFacts.fromReplay state))
                            binding
                            InputMap.empty
                        |> accepted
                        |> apply state

                    let reopened =
                        ClaimCore.Domain.Tasks.Reminder.Reopen.availability
                            environment
                            (Some(DecisionFacts.fromReplay completed))
                            (reminderBinding completed id (Revision.create 2L |> Option.get))

                    Expect.equal
                        reopened.Seeds
                        (Map.ofList
                            [
                                TaskInputId.Target InputTarget.ReminderDueDate,
                                InputValue.Text "2026-11-16"
                            ])
                        "Reopening acquires only a new due date")
            testCase "state availability never substitutes for actual input admission" (fun () ->
                let state = family ()

                Expect.contains
                    (offered environment state)
                    "case.decide"
                    "The state offers a decision"

                let selected =
                    match Registry.find (TaskId.ofLiteral "case.decide") with
                    | Some(Task.Family task) -> task
                    | _ -> failtest "Expected decision task."

                let issues =
                    selected.Decide
                        (context 3)
                        environment
                        (Some(DecisionFacts.fromReplay state))
                        (caseBinding state)
                        (decisionInputs "2027-01-01" "1000" "EUR")
                    |> expectInputs

                Expect.isFalse issues.IsEmpty "A future date still refuses")
            testCase
                "the independent financial and lifecycle matrix preserves every offered case task"
                (fun () ->
                    let zeroBase = family ()

                    let zero =
                        ClaimCore.Domain.Tasks.Case.Decide.decide
                            (context 2)
                            environment
                            (Some(DecisionFacts.fromReplay zeroBase))
                            (caseBinding zeroBase)
                            (decisionInputs "2026-02-01" "0" "EUR")
                        |> accepted
                        |> apply zeroBase

                    let common =
                        [ "case.close"; "case.void"; "case.erase"; "case.changeReference" ]

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
                                "case.correctPayment"
                            ]
                            decided () |> pay 3 "2026-02-02" "1000",
                            [
                                "case.correct"
                                "case.decide"
                                "case.removePayment"
                                "case.correctPayment"
                            ]
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
                                [
                                    "case.correct"
                                    "case.reopen"
                                    "case.void"
                                    "case.erase"
                                    "case.changeReference"
                                ])
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

let private ranked state =
    Registry.all
    |> List.choose (function
        | Task.Family task when task.Declaration.Target = ActSubject.Case ->
            Some(
                task.Declaration,
                task.Availability
                    environment
                    (Some(DecisionFacts.fromReplay state))
                    (binding task.Declaration state)
            )
        | _ -> None)

[<Tests>]
let recommendations =
    testList
        "task-owned case recommendations"
        [
            testCase "financial progress chooses decide, record payment, then close" (fun () ->
                let paid = decided () |> pay 3 "2026-03-01" "1000"

                for state, expected in
                    [
                        family (), "case.decide"
                        decided (), "case.recordPayment"
                        paid, "case.close"
                    ] do
                    Expect.equal
                        (ranked state |> Registry.recommendation |> Option.map TaskId.token)
                        (Some expected)
                        "Literal business preference")
            testCase
                "fallbacks are amend, then eligible correct, without arbitrary authoring actions"
                (fun () ->
                    let only ids state =
                        ranked state
                        |> List.filter (fun (declaration, _) ->
                            List.contains (TaskId.token declaration.Id) ids)
                        |> Registry.recommendation
                        |> Option.map TaskId.token

                    Expect.equal
                        (only [ "case.correct"; "case.amendRegistration" ] (family ()))
                        (Some "case.amendRegistration")
                        "Amend fallback"

                    Expect.equal
                        (only [ "case.correct" ] (decided ()))
                        (Some "case.correct")
                        "Eligible correction fallback"

                    Expect.isNone
                        (only
                            [ "case.open"; "case.void"; "case.reinstate"; "case.erase" ]
                            (family ()))
                        "No routine action was offered")
            testCase "all reachable inactive states have no routine recommendation" (fun () ->
                for state in [ family (); decided (); decided () |> pay 3 "2026-03-01" "1000" ] do
                    for lifecycle, status in
                        [
                            Lifecycle.Active, CaseStatus.Closed
                            Lifecycle.Voided, CaseStatus.Opened
                            Lifecycle.Voided, CaseStatus.Closed
                            Lifecycle.Erased, CaseStatus.Opened
                        ] do
                        let state = setCase lifecycle status state

                        Expect.isNone
                            (ranked state |> Registry.recommendation)
                            "Inactive or closed state")
            testCase
                "every eligible offer subset recommends an offered safe task independent of order"
                (fun () ->
                    let states = [ family (); decided (); decided () |> pay 3 "2026-03-01" "1000" ]

                    for state in states do
                        let offers =
                            ranked state
                            |> List.filter (fun (_, available) -> available.Blocked.IsNone)

                        for mask in 0 .. (1 <<< offers.Length) - 1 do
                            let subset =
                                offers
                                |> List.indexed
                                |> List.choose (fun (index, offer) ->
                                    if mask &&& (1 <<< index) <> 0 then Some offer else None)

                            let recommended = Registry.recommendation subset

                            Expect.equal
                                recommended
                                (Registry.recommendation (List.rev subset))
                                "Order independence"

                            match recommended with
                            | None -> ()
                            | Some task ->
                                Expect.isTrue
                                    (subset
                                     |> List.exists (fun (declaration, _) -> declaration.Id = task))
                                    "Offered task"

                                Expect.isFalse
                                    (List.contains
                                        (TaskId.token task)
                                        [ "case.void"; "case.reinstate"; "case.erase" ])
                                    "No destructive or stewardship surprise")
        ]
