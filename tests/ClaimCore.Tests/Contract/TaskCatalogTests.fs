module ClaimCore.Tests.TaskCatalogTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Cli
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private task words =
    CommandGrammar.operations
    |> List.find (fun task -> CommandGrammar.words task = words)

[<Tests>]
let tests =
    testList
        "semantic tasks and neutral metadata"
        [
            testCase
                "task declarations retain independently stated consequence and binding meaning"
                (fun () ->
                    for token, family, binding, consequence in
                        [
                            "case.correct",
                            ActFamily.Business,
                            BindingShape.Case,
                            Consequence.Routine
                            "case.erase",
                            ActFamily.Stewardship,
                            BindingShape.CaseErase,
                            Consequence.Irreversible
                            "case.close",
                            ActFamily.Business,
                            BindingShape.Case,
                            Consequence.Routine
                            "case.void",
                            ActFamily.Stewardship,
                            BindingShape.Case,
                            Consequence.Stewardship
                        ] do
                        let declaration =
                            ClaimCore.Domain.Tasks.Registry.findDeclaration (
                                TaskId.ofLiteral token
                            )
                            |> Option.get

                        Expect.equal
                            (declaration.Family,
                             TaskDeclaration.bindingShape declaration,
                             declaration.Consequence)
                            (family, binding, consequence)
                            "Domain declaration semantics")
            testCase "role principal choices retain the independent human-only policy" (fun () ->
                let both = [ PrincipalKind.Human; PrincipalKind.Service ]

                let expected =
                    Map.ofList
                        [
                            Role.Owner, [ PrincipalKind.Human ]
                            Role.CaseReader, both
                            Role.CaseRegistrar, both
                            Role.CaseHandler, both
                            Role.PaymentRecorder, both
                            Role.DataSteward, [ PrincipalKind.Human ]
                            Role.Auditor, both
                        ]

                let actual =
                    Catalog.document.Roles
                    |> List.map (fun role -> role.Role, role.AdmittedPrincipalKinds)
                    |> Map.ofList

                Expect.equal actual expected "No UI copy of human-only admission"
                Expect.equal Catalog.document.Roles.Length expected.Count "One row per role")
            testCase
                "every actor operation and endpoint has one semantic task with no provisioning command"
                (fun () ->
                    let acts =
                        TaskDeclarations.all
                        |> List.choose (fun declaration -> declaration.Ledger |> Option.map _.Id)

                    let expected =
                        ClaimCore.Domain.Tasks.Registry.clientDeclarations |> List.map _.Id

                    Expect.equal
                        (Set.ofList acts)
                        (Set.ofList expected)
                        "Every actor-exposed ledger declaration"

                    Expect.equal acts.Length 29 "Provisioning is operator-only"

                    Expect.equal
                        acts.Length
                        (List.distinct acts).Length
                        "No duplicate operation tasks"

                    let endpoints =
                        Api.operationPreview.Token
                        :: (TaskDeclarations.all
                            |> List.map (fun task ->
                                TaskRouting.endpoint task.Id |> ActorEndpoint.token))
                        |> Set.ofList

                    let expected =
                        Api.all
                        |> List.map _.Token
                        |> List.filter (fun token ->
                            token <> "session" && token <> "session.logout")
                        |> Set.ofList

                    Expect.equal endpoints expected "Host session bootstrap is separate")
            testCase
                "CLI input selection and correction follow semantic groups without wire paths"
                (fun () ->
                    let fields task =
                        CommandGrammar.inputs task
                        |> List.choose (fun input ->
                            match input.Name with
                            | TaskInputId.Target(InputTarget.Field field) when
                                field <> BusinessField.CaseReference
                                ->
                                Some(BusinessField.key field)
                            | _ -> None)

                    Expect.equal
                        (fields (task [ "case"; "open" ]))
                        (Inputs.fieldKeys "registration")
                        "Registration acquisition"

                    Expect.equal
                        (fields (task [ "case"; "void" ]))
                        []
                        "Reason act has no registration inputs"

                    Expect.equal
                        (fields (task [ "case"; "correct" ]))
                        (Inputs.fieldKeys "registration" @ Inputs.fieldKeys "decision")
                        "Correction does not rewrite payment entries"

                    for task in CommandGrammar.operations do
                        Expect.stringStarts
                            (CommandGrammar.synopsis task)
                            (String.concat " " (CommandGrammar.words task))
                            "Actual grammar words")
            testCase "neutral catalog round trips without operation binding entries" (fun () ->
                let value = Catalog.document
                let encoded = CatalogCodecs.codec.Write value

                Expect.isNone
                    (JsonValue.tryMember "entries" encoded)
                    "Retired UX catalogs are absent"

                Expect.equal
                    (Codec.encode CatalogCodecs.codec value
                     |> Codec.decode Limits.MaximumResponseBytes CatalogCodecs.codec)
                    (Ok value)
                    "Current neutral codec")
            testCase
                "clearable metadata describes updates rather than creation-time absence"
                (fun () ->
                    let inputs act =
                        (TaskDeclarations.describe (TaskId.token act)).Inputs

                    for act in
                        [
                            (TaskId.ofLiteral "case.open")
                            (TaskId.ofLiteral "people.register")
                            (TaskId.ofLiteral "people.admit")
                        ] do
                        Expect.isFalse
                            (inputs act |> List.exists _.Clearable)
                            "Creation has no previously stored value to clear"

                    let contact =
                        inputs ((TaskId.ofLiteral "people.setProfile"))
                        |> List.find (fun input ->
                            input.Input = TaskInputId.Target InputTarget.ProfileContact)

                    Expect.isTrue contact.Clearable "Profile updates can clear existing contact"

                    let narrative =
                        inputs ((TaskId.ofLiteral "case.amendRegistration"))
                        |> List.find (fun input ->
                            input.Input =
                                TaskInputId.Target(
                                    InputTarget.Field BusinessField.IncidentDescription
                                ))

                    Expect.isTrue
                        narrative.Clearable
                        "Optional registration updates can clear narrative")
        ]
