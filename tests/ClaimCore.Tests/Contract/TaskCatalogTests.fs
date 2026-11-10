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
                "case effects and affected sections retain independently stated Domain meaning"
                (fun () ->
                    for kind, family, sections, effect in
                        [
                            CaseActKind.Correct,
                            ActFamily.Business,
                            [ CaseSection.Registration; CaseSection.Decision ],
                            CaseEffect.RecordFacts
                            CaseActKind.Erase,
                            ActFamily.Stewardship,
                            [
                                CaseSection.Registration
                                CaseSection.Decision
                                CaseSection.Payment
                                CaseSection.Work
                            ],
                            CaseEffect.ErasePayload
                            CaseActKind.Close, ActFamily.Business, [], CaseEffect.ChangeStatus
                            CaseActKind.Void, ActFamily.Stewardship, [], CaseEffect.ChangeLifecycle
                        ] do
                        let description = CaseActKind.describe kind

                        Expect.equal
                            (description.Family, description.AffectedSections, description.Effect)
                            (family, sections, effect)
                            "Core owns business meaning independently of transport")
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
                        TaskId.all
                        |> List.choose (function
                            | TaskId.Operation act -> Some act
                            | _ -> None)

                    let expected =
                        ActKind.all
                        |> List.filter ((<>) (ActKind.Authority AuthorityKind.ProvisionOwner))

                    Expect.equal
                        (Set.ofList acts)
                        (Set.ofList expected)
                        "All authored operation families"

                    Expect.equal
                        acts.Length
                        (List.distinct acts).Length
                        "No duplicate operation tasks"

                    let endpoints =
                        Api.operationPreview.Token
                        :: (TaskId.all |> List.map (TaskRouting.endpoint >> ActorEndpoint.token))
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
        ]
