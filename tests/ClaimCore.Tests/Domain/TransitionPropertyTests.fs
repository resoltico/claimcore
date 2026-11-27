module ClaimCore.Tests.TransitionPropertyTests

open System
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Tasks
open ClaimCore.Tests.PropertyHarness

module Oracle = ClaimCore.Tests.TransitionOracle

type private Step =
    { Witness: Oracle.Witness; Stale: bool }

let private today = DateOnly(2026, 9, 7)

let private required result =
    result
    |> Result.defaultWith (fun issue -> failtestf "Sequence fixture: %A" issue)

let private selected (witness: Oracle.Witness) =
    match Registry.find (TaskId.ofLiteral witness.Task) with
    | Some task -> task
    | _ -> failtest "Expected a case task."

let private context number sequence =
    let context = ClaimCore.Tests.TaskBuilders.context number

    { context with
        Decision =
            { context.Decision with
                BusinessDate = Some today
                Lifetime = DecisionLifetime.Recorded
            }
        Stamp =
            { context.Stamp with
                Position = EffectPosition.create sequence 0 |> Option.get
            }
    }

let private initialChanges () =
    let context = context 1 Sequence.first

    let context =
        { context with
            Stamp =
                { context.Stamp with
                    OperationId = Oracle.origin
                }
        }

    ClaimCore.Domain.Tasks.Case.Open.admit
        context
        ClaimCore.Tests.TaskBuilders.environment
        None
        (Binding.CaseNew Revision.zero)
        Oracle.opening.Inputs
    |> Result.bind (ClaimCore.Domain.Tasks.Case.Open.accept Oracle.reference None)
    |> required

let private initial () =
    initialChanges ()
    |> List.fold (fun state change -> Some(CaseFamily.apply state change |> required)) None
    |> Option.get

let private binding (model: Oracle.Model) stale witness =
    let expected =
        Revision.create (Revision.value model.Snapshot.Revision - (if stale then 1L else 0L))
        |> Option.get

    let target = CaseSelector.Id model.Snapshot.CaseId

    match TaskDeclaration.bindingShape (Task.declaration (selected witness)) with
    | BindingShape.CaseNew -> Binding.CaseNew Revision.zero
    | BindingShape.Case -> Binding.Case(target, expected)
    | BindingShape.CasePayment ->
        Binding.CasePayment(
            target,
            expected,
            model.Payments
            |> List.tryHead
            |> Option.defaultValue (ClaimCore.Tests.TaskBuilders.operation 999)
        )
    | BindingShape.CaseErase -> Binding.CaseErase(target, expected, model.Sequence)
    | _ -> failtest "The sequence vocabulary contains only case tasks."

let private decide number sequence (current: CaseFamily) model stale (witness: Oracle.Witness) =
    let context = context number sequence
    let environment = ClaimCore.Tests.TaskBuilders.environment
    let binding = binding model stale witness

    match selected witness with
    | Task.Opening task ->
        task.Admit context environment None binding witness.Inputs
        |> Result.bind (task.Accept current.Reference (Some current.Id))
    | Task.Family task ->
        task.Decide
            context
            environment
            (Some(DecisionFacts.fromReplay current))
            binding
            witness.Inputs
    | Task.Person _ -> failtest "Expected a case task."

let private execute number sequence current model stale witness =
    decide number sequence current model stale witness
    |> Result.bind (fun changes ->
        changes
        |> List.fold
            (fun state change ->
                state
                |> Result.bind (fun state ->
                    CaseFamily.apply (Some state) change
                    |> Result.mapError Rejection.InvalidState))
            (Ok current))

let private actualSnapshot state =
    CaseFamily.snapshot state
    |> Option.defaultWith (fun () -> failtest "The sequence retains a complete case view.")

let private checkSequence steps =
    let start = initial ()

    let mutable acceptedChanges = initialChanges ()

    let _, _, valid =
        steps
        |> List.indexed
        |> List.fold
            (fun (current, model, valid) (index, step) ->
                let number = index + 100
                let sequence = Sequence.create (int64 index + 2L) |> Option.get

                let expected =
                    Oracle.advance
                        model
                        step.Stale
                        step.Witness
                        (ClaimCore.Tests.TaskBuilders.operation number)
                        sequence

                let decision = decide number sequence current model step.Stale step.Witness

                match decision with
                | Ok changes -> acceptedChanges <- acceptedChanges @ changes
                | Error _ -> ()

                let result = execute number sequence current model step.Stale step.Witness

                let replayed =
                    acceptedChanges
                    |> List.fold
                        (fun state change -> Some(CaseFamily.apply state change |> required))
                        None
                    |> Option.get

                Expect.equal
                    replayed
                    (result |> Result.defaultValue current)
                    "Accepted case prefixes replay through generic apply"

                let matches = Oracle.matches expected (result |> Result.map actualSnapshot)

                let sequenceMatches =
                    match expected, result with
                    | Some next, Ok actual -> actual.Sequence = next.Sequence
                    | _ -> true

                result |> Result.defaultValue current,
                expected |> Option.defaultValue model,
                valid && matches && sequenceMatches)
            (start, Oracle.initial, actualSnapshot start = Oracle.initial.Snapshot)

    valid

let private sample task =
    Oracle.samples |> List.find (fun witness -> witness.Task = task)

let private acceptingPath =
    [
        "case.decide"
        "case.recordPayment"
        "case.close"
        "case.reopen"
        "case.removePayment"
        "case.withdrawDecision"
        "case.amendRegistration"
        "case.close"
        "case.close"
        "case.reopen"
        "case.void"
        "case.close"
        "case.reinstate"
        "case.erase"
        "case.open"
    ]
    |> List.map (fun task -> { Witness = sample task; Stale = false })

let private sequenceProperty =
    property {
        let! steps =
            Gen.list
                (Range.constant 1 (sequenceMaximum ()))
                (gen {
                    let! witness = Gen.item (Oracle.decide "0" :: Oracle.samples)
                    let! stale = Gen.bool
                    return { Witness = witness; Stale = stale }
                })

        return checkSequence steps
    }

let private negativeControls () =
    let next task model number =
        Oracle.advance
            model
            false
            (sample task)
            (ClaimCore.Tests.TaskBuilders.operation number)
            (Sequence.create (int64 number) |> Option.get)
        |> Option.get

    let decided = next "case.decide" Oracle.initial 2
    let paid = next "case.recordPayment" decided 3
    let closed = next "case.close" paid 4

    let withoutPayments (snapshot: CaseSnapshot) =
        let fields = snapshot.State |> CaseState.fields |> Option.get

        let finance = FinancialPosition.create fields.Decision Map.empty 0L |> required

        { snapshot with
            State = CaseState.Active { fields with Finance = finance }
        }

    for expected, actual in
        [
            Some decided, Error(Refusal.Access AccessRefusal.ResourceUnavailable)
            Some paid, Ok(withoutPayments paid.Snapshot)
            Some paid,
            Ok
                { paid.Snapshot with
                    Revision = decided.Snapshot.Revision
                }
            Some closed, Ok(withoutPayments closed.Snapshot)
            None, Ok paid.Snapshot
        ] do
        Expect.isFalse
            (Oracle.matches expected actual)
            "The independent oracle rejects refusal-only and corrupt accepted outcomes"

let private reach witnesses =
    let actual, model =
        witnesses
        |> List.indexed
        |> List.fold
            (fun (actual, model) (index, witness) ->
                let number = index + 300
                let sequence = Sequence.create (int64 index + 2L) |> Option.get

                let next =
                    Oracle.advance
                        model
                        false
                        witness
                        (ClaimCore.Tests.TaskBuilders.operation number)
                        sequence
                    |> Option.get

                let actual = execute number sequence actual model false witness |> required
                Expect.equal (actualSnapshot actual) next.Snapshot "Reachable-state witness"
                actual, next)
            (initial (), Oracle.initial)

    actual, model

let private availabilityProperty =
    property {
        let! witnesses =
            Gen.item
                [
                    []
                    [ Oracle.decide "750" ]
                    [ Oracle.decide "0" ]
                    [ Oracle.decide "750"; Oracle.payment ]
                    [ sample "case.close" ]
                    [ Oracle.decide "750"; sample "case.close" ]
                    [ Oracle.decide "750"; Oracle.payment; sample "case.close" ]
                    [ sample "case.void" ]
                    [ sample "case.erase" ]
                ]

        let actual, model = reach witnesses
        let expected = Oracle.offered model.Snapshot.State

        let offers =
            Registry.all
            |> List.choose (function
                | Task.Family task when
                    task.Declaration.Target = ActSubject.Case
                    && task.Declaration.Exposure = TaskExposure.Client
                    ->
                    let binding =
                        match TaskDeclaration.bindingShape task.Declaration with
                        | BindingShape.CaseNew -> Binding.CaseNew Revision.zero
                        | BindingShape.CaseErase ->
                            Binding.CaseErase(
                                CaseSelector.Id actual.Id,
                                actual.Case.Revision,
                                actual.Sequence
                            )
                        | _ -> Binding.Case(CaseSelector.Id actual.Id, actual.Case.Revision)

                    if
                        (task.Availability
                            ClaimCore.Tests.TaskBuilders.environment
                            (Some(DecisionFacts.fromReplay actual))
                            binding)
                            .Blocked.IsNone
                    then
                        Some(TaskId.token task.Declaration.Id)
                    else
                        None
                | _ -> None)
            |> Set.ofList

        let executionMatches =
            Oracle.samples
            |> List.mapi (fun index witness ->
                let sequence = Sequence.successor model.Sequence

                let accepted =
                    execute (500 + index) sequence actual model false witness |> Result.isOk

                accepted = Set.contains witness.Task expected)
            |> List.forall id

        return offers = expected && executionMatches
    }

[<Tests>]
let tests =
    testList
        "independent case sequence oracle"
        [
            testCase
                "accepted and refused sequences match complete independent state and family sequence"
                (fun () ->
                    Expect.isTrue (checkSequence acceptingPath) "Fixed accepting and refused path"
                    run "case sequence" sequenceProperty)
            testCase
                "sequence oracle rejects refusal-only and corrupt accepted outcomes"
                negativeControls
            testCase "reachable states match the independent availability matrix" (fun () ->
                run "case availability" availabilityProperty)
        ]
