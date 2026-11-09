module ClaimCore.Tests.AssigneeDocumentTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.TestSupport

let private actor number =
    ActorId.create (Guid.Parse(sprintf "00000000-0000-4000-8000-%012d" number))
    |> Option.get

let private document () =
    HumanReadDocuments.assignees
        {
            Basis = Inputs.workBasis
            Items = [ Inputs.actorReference (actor 1); Inputs.actorReference (actor 2) ]
            NextCursor = Some "choice-next"
        }

[<Tests>]
let tests =
    testList
        "core assignee choice documents"
        [
            testCase
                "choice IDs bind exact authored rows and opaque captions survive independently"
                (fun () ->
                    let original = document ()

                    let context =
                        match original.Context with
                        | InteractionContext.Assignees value -> value
                        | _ -> failtest "Choice handles required"

                    Expect.equal
                        context.ActorIds
                        [ actor 1; actor 2 ]
                        "Ordered identities only in context"

                    Expect.equal
                        (HumanDocument.nextCursor original)
                        (Some "choice-next")
                        "Native paging uses dedicated choice context"

                    let caption (region: Region) =
                        match region.Content with
                        | RegionContent.Table table when region.Role = RegionRole.Results ->
                            let rows =
                                table.Rows
                                |> List.map (fun row ->
                                    { row with
                                        Values =
                                            [ PresentedValue.Text("opaque-caption:" + row.Key) ]
                                    })

                            { region with
                                Content = RegionContent.Table { table with Rows = rows }
                            }
                        | _ -> region

                    let opaque =
                        { original with
                            Screen =
                                {
                                    Regions = original.Screen.Regions |> List.map caption
                                }
                        }

                    let bytes = Codec.encode HumanDocumentCodecs.document opaque

                    Expect.isOk
                        (Codec.decode 65536 HumanDocumentCodecs.document bytes)
                        "Caption content is core presentation, not reconstructed ActorRef semantics"

                    Expect.stringContains
                        (Encoding.UTF8.GetString bytes)
                        "opaque-caption:"
                        "Opaque caption remains authoritative"

                    Expect.equal
                        (match opaque.Context with
                         | InteractionContext.Assignees value -> value.ActorIds
                         | _ -> [])
                        context.ActorIds
                        "Changing presentation never changes selected IDs")
            testCase
                "missing, extra, duplicate, reordered and undisclosed choice handles refuse structurally"
                (fun () ->
                    let bytes = Codec.encode HumanDocumentCodecs.document (document ())

                    let mutate edit =
                        let node = JsonNode.Parse(Encoding.UTF8.GetString bytes) |> nonNull
                        edit node
                        Encoding.UTF8.GetBytes(node.ToJsonString())

                    let actors (node: JsonNode) =
                        let context = node["context"] |> nonNull
                        let values = context["actorIds"] |> nonNull
                        values.AsArray()

                    let text (value: string) =
                        System.Text.Json.Nodes.JsonValue.Create value

                    let invalid =
                        [
                            mutate (fun node -> (actors node).RemoveAt 0)
                            mutate (fun node -> (actors node).Add(ActorId.format (actor 3)))
                            mutate (fun node ->
                                (actors node)[1] <- text (ActorId.format (actor 1)))
                            mutate (fun node ->
                                let ids = actors node
                                ids[0] <- text (ActorId.format (actor 2))
                                ids[1] <- text (ActorId.format (actor 1)))
                            mutate (fun node -> node["disclosure"] <- text "WITHHELD")
                        ]

                    for raw in invalid do
                        Expect.isError
                            (Codec.validate 65536 HumanDocumentCodecs.document raw)
                            "Native structural guard detects inconsistent identity/presentation"

                        Expect.isError
                            (Codec.decode 65536 HumanDocumentCodecs.document raw)
                            "Typed read applies the same boundary")
        ]
