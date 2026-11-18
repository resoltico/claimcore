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

                for token in [ "people.provisionOwner"; "people.recoverOwner" ] do
                    let operator = declaration token

                    Expect.equal
                        operator.Exposure
                        TaskExposure.Operator
                        "Owner administration is operator-only"

                    Expect.isFalse
                        (Registry.clientDeclarations
                         |> List.exists (fun task -> task.Id = operator.Id))
                        "Owner administration is unavailable through a client")
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
            testCase
                "creation references and scope selection retain their public identities"
                (fun () ->
                    let reference =
                        TaskInputId.Target(InputTarget.Field BusinessField.CaseReference)

                    let created = declaration "case.open" |> input reference

                    Expect.equal
                        created.Source
                        InputSource.Authored
                        "The creation reference is authored"

                    Expect.equal
                        created.Retention
                        InputRetention.Retained
                        "The reference stays reserved and disclosed after family erasure"

                    let claimant =
                        declaration "case.open"
                        |> input (TaskInputId.Target(InputTarget.Field BusinessField.PolicyNumber))

                    Expect.equal
                        claimant.Retention
                        InputRetention.Erasable
                        "Private authored registration remains erasable"

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

                    let whenCase =
                        role.ChoicesWhen |> List.find (fun rule -> rule.Input = TaskInputId.Scope)

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
                        "Owner is installation-wide; the other roles have case capabilities"

                    let service =
                        role.ChoicesWhen |> List.find (fun rule -> rule.Value = "SERVICE")

                    Expect.equal
                        (Set.ofList service.Choices)
                        (Set.ofList
                            [
                                "CASE_READER"
                                "CASE_REGISTRAR"
                                "CASE_HANDLER"
                                "PAYMENT_RECORDER"
                                "AUDITOR"
                            ])
                        "Service admission excludes human-only roles"

                    let revoked =
                        declaration "people.revoke" |> input (TaskInputId.Target InputTarget.Role)

                    Expect.isEmpty
                        revoked.ChoicesWhen
                        "Revocation can select recognized stored roles regardless of admission"

                    let admitted =
                        (declaration "people.admit").Collections.Head.Inputs
                        |> List.find (fun value ->
                            value.Input = TaskInputId.Target InputTarget.Role)

                    Expect.equal
                        admitted.ChoicesWhen
                        role.ChoicesWhen
                        "Initial and later assignment share admission constraints")
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
            testCase
                "declarations preserve capability-based human requester requirements"
                (fun () ->
                    let expected =
                        Set.ofList
                            [
                                "case.void"
                                "case.reinstate"
                                "case.erase"
                                "people.register"
                                "people.admit"
                                "people.grant"
                                "people.revoke"
                                "people.enable"
                                "people.disable"
                                "people.setProfile"
                                "people.changeSignInIdentity"
                                "people.eraseProfile"
                                "work.update"
                                "note.add"
                                "note.remove"
                                "link.add"
                                "link.remove"
                                "reminder.create"
                                "reminder.update"
                                "reminder.complete"
                                "reminder.cancel"
                                "reminder.reopen"
                                "reminder.completeAndPlanNext"
                            ]

                    let actual =
                        Registry.declarations
                        |> List.filter _.RequiresHumanRequester
                        |> List.map (fun declaration -> TaskId.token declaration.Id)
                        |> Set.ofList

                    Expect.equal
                        actual
                        expected
                        "Domain capability policy determines the requester classification"

                    Expect.isTrue
                        (Registry.clientDeclarations
                         |> List.forall (fun declaration -> not declaration.Capabilities.IsEmpty))
                        "Client tasks have declared capability needs"

                    let operator =
                        Registry.findDeclaration (TaskId.ofLiteral "people.provisionOwner")
                        |> Option.get

                    Expect.isEmpty
                        operator.Capabilities
                        "Operator provisioning has no pre-existing actor capability"

                    Expect.isFalse
                        operator.RequiresHumanRequester
                        "The operator path is distinct from requester admission")
        ]
