module ClaimCore.Tests.CurrentGroupDocumentTests

open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private caseRead () =
    let original = ResponseExamples.caseRead
    let fields = CaseState.fields original.Snapshot.State |> Option.get

    let finance =
        FinancialPosition.withDecision
            (Some(Inputs.admittedDecision Inputs.decision))
            fields.Finance
        |> Result.defaultWith (fun error -> failtestf "%A" error)

    let state = CaseState.Active { fields with Finance = finance }

    let blocked =
        CaseActKind.all
        |> List.filter ((<>) CaseActKind.Correct)
        |> List.map (fun act ->
            {
                CaseActRefusal.Act = act
                Reason = Refusal.Access(AccessRefusal.NotAuthorized Capability.Decide)
            })

    let advice =
        CaseAdvice.create
            [ CaseActKind.Correct ]
            (Some
                {
                    Registration = [ CorrectionGroupAction.Keep; CorrectionGroupAction.Update ]
                    Decision = [ CorrectionGroupAction.Keep; CorrectionGroupAction.Update ]
                })
            HistoryAccess.None
            blocked
        |> Result.defaultWith failtest

    { original with
        Snapshot = { original.Snapshot with State = state }
        Advice = advice
    }

[<Tests>]
let tests =
    testList
        "core current correction groups"
        [
            testCase
                "offered correction groups carry saved core presentation and preserve opaque captions independently of seeds"
                (fun () ->
                    let original = HumanCaseDocument.ofCore (caseRead ())

                    Expect.equal
                        (original.Screen.CurrentGroups |> List.map _.Group)
                        [ RecordGroup.Registration; RecordGroup.Decision ]
                        "Semantic identity is independent of translated headings"

                    let registration = original.Screen.CurrentGroups.Head.Section

                    Expect.isTrue
                        (registration.Entries
                         |> List.exists (fun entry -> entry.Key = "claimantName"))
                        "Full registration before presentation is supplied"

                    let decision = original.Screen.CurrentGroups[1].Section

                    Expect.isTrue
                        (decision.Entries |> List.exists (fun entry -> entry.Key = "payableAmount"))
                        "Decision includes atomic money presentation"

                    let opaque =
                        { original with
                            Screen =
                                { original.Screen with
                                    CurrentGroups =
                                        original.Screen.CurrentGroups
                                        |> List.map (fun group ->
                                            { group with
                                                Section =
                                                    { group.Section with
                                                        Entries =
                                                            [
                                                                ScreenEntries.value
                                                                    "opaque-before"
                                                                    "label.details"
                                                                    Prominence.Primary
                                                                    (PresentedValue.Text
                                                                        "core-before-caption Ω")
                                                            ]
                                                    }
                                            })
                                }
                        }

                    let bytes = Codec.encode HumanDocumentCodecs.document opaque

                    Expect.isOk
                        (Codec.decode 65536 HumanDocumentCodecs.document bytes)
                        "No raw-state reconstruction is required to admit the saved caption"

                    Expect.stringContains
                        (Encoding.UTF8.GetString bytes)
                        "core-before-caption"
                        "Opaque presentation is retained"

                    Expect.equal
                        opaque.Context
                        original.Context
                        "Presentation changes never become editable input seeds")
            testCase
                "erased and unoffered groups are absent and invalid cross-references refuse"
                (fun () ->
                    let original = caseRead ()

                    let hidden =
                        { original with
                            Advice = Inputs.caseAdvice original.Snapshot.State
                        }

                    Expect.isEmpty
                        (HumanCaseDocument.ofCore hidden).Screen.CurrentGroups
                        "No unoffered before-data expansion"

                    let erased =
                        { original with
                            Snapshot =
                                { original.Snapshot with
                                    State = CaseState.Erased
                                }
                            Advice = Inputs.caseAdvice CaseState.Erased
                        }

                    Expect.isEmpty
                        (HumanCaseDocument.ofCore erased).Screen.CurrentGroups
                        "Erasure removes current group presentation"

                    let bytes =
                        Codec.encode
                            HumanDocumentCodecs.document
                            (HumanCaseDocument.ofCore original)

                    let mutate change =
                        let node = JsonNode.Parse(Encoding.UTF8.GetString bytes) |> nonNull
                        change node
                        Encoding.UTF8.GetBytes(node.ToJsonString())

                    let groups node =
                        ((node: JsonNode)["screen"] |> nonNull)["currentGroups"]
                        |> nonNull
                        |> _.AsArray()

                    let invalid =
                        [
                            mutate (fun node ->
                                let values = groups node
                                let first = values[0] |> nonNull
                                values[1] <- first.DeepClone())
                            mutate (fun node ->
                                let context = node["context"] |> nonNull
                                let correction = context["correction"] |> nonNull
                                let keep = JsonArray()
                                keep.Add "KEEP"
                                correction["decision"] <- keep)
                            mutate (fun node ->
                                node["disclosure"] <-
                                    System.Text.Json.Nodes.JsonValue.Create "WITHHELD"

                                let context = node["context"] |> nonNull
                                let seeds = context["seeds"] |> nonNull
                                seeds["registration"] <- null
                                seeds["decision"] <- null
                                seeds["work"] <- null
                                context["editableInputs"] <- JsonArray())
                        ]

                    for raw in invalid do
                        Expect.isError
                            (Codec.validate 65536 HumanDocumentCodecs.document raw)
                            "Groups must be unique, offered and currently disclosed")
        ]
