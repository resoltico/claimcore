module ClaimCore.Tests.Domain.TaskRegistryTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Tasks

let private declaration token =
    TaskId.ofLiteral token |> Registry.findDeclaration |> Option.get

let private input name (task: TaskDeclaration) =
    task.Inputs |> List.find (fun input -> input.Input = name)

[<Tests>]
let tests =
    testList
        "declared tasks and input maps"
        [
            testCase "task identity is opaque and admits the complete token only" (fun () ->
                for token in
                    [ "case.open"; "reminder.completeAndPlanNext"; "people.provisionOwner" ] do
                    Expect.equal
                        (TaskId.tryParse token |> Option.map TaskId.token)
                        (Some token)
                        "A valid token is preserved"

                for token in
                    [
                        ""
                        "case"
                        "Case.open"
                        "case.Open"
                        "case.open\n"
                        "case.open "
                        ".case.open"
                        "case.open."
                    ] do
                    Expect.isNone (TaskId.tryParse token) "The entire token must match"

                let unknown = TaskId.tryParse "case.notDeclared" |> Option.get

                Expect.isNone
                    (Registry.findDeclaration unknown)
                    "Parsing does not invent registry membership")
            testCase "operator exposure and identity uniqueness come from the registry" (fun () ->
                let ids = Registry.declarations |> List.map _.Id

                Expect.equal
                    (ids |> Set.ofList |> Set.count)
                    ids.Length
                    "Duplicate task identities cannot collapse silently"

                let owner = declaration "people.provisionOwner"

                Expect.equal
                    owner.Exposure
                    TaskExposure.Operator
                    "Initialization is an operator task"

                Expect.isFalse
                    (Registry.clientDeclarations |> List.exists (fun task -> task.Id = owner.Id))
                    "The initializer is not a client operation")
            testCase "creation is about the selected target, including child targets" (fun () ->
                for token in
                    [
                        "case.open"
                        "people.register"
                        "people.admit"
                        "people.provisionOwner"
                        "note.add"
                        "reminder.create"
                    ] do
                    Expect.isTrue (declaration token).Creates "This task creates its target"

                for token in [ "work.update"; "note.remove"; "reminder.completeAndPlanNext" ] do
                    Expect.isFalse
                        (declaration token).Creates
                        "Updates and compound actions keep an existing binding"

                Expect.isTrue
                    (declaration "case.erase").ReviewsFamilySequence
                    "Erasure reviews the complete family sequence"

                Expect.isFalse
                    (declaration "case.close").ReviewsFamilySequence
                    "Closing reviews the case part")
            testCase "creation reference and scope selection have distinct retention" (fun () ->
                let reference = TaskInputId.Target(InputTarget.Field BusinessField.CaseReference)
                let created = declaration "case.open" |> input reference

                Expect.equal
                    created.Source
                    InputSource.Authored
                    "The creation reference is authored"

                Expect.equal
                    created.Retention
                    InputRetention.Erasable
                    "Authored creation inputs follow their declared retention"

                let bound = declaration "case.close" |> input reference

                Expect.equal
                    bound.Source
                    InputSource.Binding
                    "Existing selection stays in the binding"

                let scoped = declaration "people.grant" |> input reference

                Expect.equal
                    scoped.Retention
                    InputRetention.Retained
                    "Role scope selection survives profile erasure"

                let role =
                    declaration "people.grant" |> input (TaskInputId.Target InputTarget.Role)

                let whenCase = role.ChoicesWhen |> Option.get

                Expect.equal
                    (whenCase.Input, whenCase.Value)
                    (TaskInputId.Scope, "CASE")
                    "The condition names the authored scope"

                Expect.equal
                    (Set.ofList whenCase.Choices)
                    (Set.ofList
                        [
                            "CASE_READER"
                            "CASE_REGISTRAR"
                            "CASE_HANDLER"
                            "PAYMENT_RECORDER"
                            "DATA_STEWARD"
                            "AUDITOR"
                        ])
                    "Owner is installation-wide; the other roles have case capabilities")
            testCase
                "null and omission stay distinct and rows retain their submitted ordinals"
                (fun () ->
                    let reference =
                        TaskInputId.Target(InputTarget.Field BusinessField.CaseReference)

                    let values = Map.ofList [ reference, InputValue.Null ]

                    let rows =
                        [
                            Map.ofList [ TaskInputId.Scope, InputValue.Text "INSTALLATION" ]
                            Map.ofList [ TaskInputId.Scope, InputValue.Null ]
                        ]

                    let submitted =
                        InputMap.create values (Map.ofList [ TaskCollectionId.PeopleGrants, rows ])
                        |> Option.get

                    Expect.equal
                        (Map.tryFind reference InputMap.empty.Values)
                        None
                        "Missing is not a supplied null"

                    Expect.equal
                        (Map.tryFind reference submitted.Values)
                        (Some InputValue.Null)
                        "An explicit null is preserved"

                    let rows = submitted.Collections[TaskCollectionId.PeopleGrants]

                    Expect.equal
                        (rows |> List.map _.Ordinal)
                        [ 0; 1 ]
                        "Collection order supplies stable ordinals"

                    Expect.equal
                        (Map.tryFind TaskInputId.Scope rows[1].Values)
                        (Some InputValue.Null)
                        "Row values retain null as well"

                    Expect.isNone
                        (InputMap.create
                            Map.empty
                            (Map.ofList
                                [ TaskCollectionId.PeopleGrants, List.replicate 49 Map.empty ]))
                        "A grant collection cannot produce ordinal 48")
        ]
