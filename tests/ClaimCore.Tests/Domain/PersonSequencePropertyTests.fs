module ClaimCore.Tests.PersonSequencePropertyTests

open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Tasks
open ClaimCore.Tests.TaskBuilders
open ClaimCore.Tests.PropertyHarness

let private fold changes =
    changes
    |> List.fold
        (fun state change ->
            state |> Result.bind (fun state -> Person.apply state change |> Result.map Some))
        (Ok None)

let private check steps =
    let created =
        ClaimCore.Domain.Tasks.People.Register.decide
            (context 2)
            (peopleEnvironment newPrincipal)
            None
            (Binding.PeopleNew Revision.zero)
            (peopleInputs newPrincipal)
        |> accepted

    let mutable records = created
    let mutable current = fold created |> accepted |> Option.get
    let mutable enabled = true
    let mutable name = Some "New Person"
    let mutable granted = false

    for index, step in List.indexed steps do
        let number = index + 3

        let task, values =
            match step with
            | 0 ->
                enabled <- not enabled
                (if enabled then "people.enable" else "people.disable"), inputs []
            | 1 ->
                granted <- not granted

                (if granted then "people.grant" else "people.revoke"),
                inputs
                    [
                        text InputTarget.Role "CASE_READER"
                        TaskInputId.Scope, InputValue.Text "INSTALLATION"
                    ]
            | 2 ->
                name <- Some(sprintf "Replacement %d" number)

                "people.setProfile",
                inputs
                    [
                        text InputTarget.ProfileDisplayName name.Value
                        TaskInputId.Target InputTarget.ProfileContact, InputValue.Null
                    ]
            | _ ->
                name <- None
                "people.eraseProfile", inputs []

        let selected =
            match Registry.find (TaskId.ofLiteral task) with
            | Some(Task.Person task) -> task
            | _ -> failtest "Person task"

        let changes =
            selected.Decide
                (context number)
                (peopleEnvironment newPrincipal)
                (Some current)
                (personBinding current)
                values
            |> accepted

        let next = personApply current changes
        records <- records @ changes
        let replayed = fold records |> accepted |> Option.get
        Expect.equal replayed next "Every accepted prefix independently folds to the live person"
        Expect.equal next.Actor.Enabled enabled "Enablement follows generated operations"

        Expect.equal
            (next.Actor.Profile
             |> Option.map (fun profile -> PartyName.format profile.DisplayName))
            name
            "Erasure and replacement have distinct presence"

        Expect.equal
            (next.Grants |> Map.keys |> Seq.toList)
            (if granted then
                 [
                     {
                         ActorGrant.Role = Role.CaseReader
                         Scope = Scope.Installation
                     }
                 ]
             else
                 [])
            "Grant state follows the independent toggle model"

        Expect.equal
            (Revision.value next.Revision)
            (int64 index + 2L)
            "One person revision per accepted operation"

        Expect.equal next.Actor.Principal newPrincipal "Immutable principal"
        current <- next

    records

[<Tests>]
let tests =
    testList
        "person accepted sequence replay"
        [
            testCase
                "generated profile presence grants and enablement prefixes replay to independently expected state"
                (fun () ->
                    run
                        "person accepted sequence replay"
                        (property {
                            let! steps =
                                Gen.list
                                    (Range.constant 1 (sequenceMaximum ()))
                                    (Gen.int32 (Range.constant 0 3))

                            check steps |> ignore
                            return true
                        }))
            testCase
                "replay rejects a missing predecessor and a corrupt profile-erasure effect"
                (fun () ->
                    let records = check [ 2; 3; 2; 1; 0 ]
                    let missing = records.Head :: (records |> List.skip 2)
                    Expect.isError (fold missing) "A missing revision cannot be replayed"

                    let corrupt =
                        records
                        |> List.map (fun change ->
                            if change.Erases.IsSome then
                                { change with Erases = None }
                            else
                                change)

                    Expect.isError
                        (fold corrupt)
                        "A disappearing profile requires an erasing effect")
        ]
