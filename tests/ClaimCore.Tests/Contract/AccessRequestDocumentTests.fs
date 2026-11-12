module ClaimCore.Tests.AccessRequestDocumentTests

open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private principal =
    PrincipalKey.human "https://issuer.example/" "request-handle"
    |> Result.defaultWith (string >> failtest)

let private document name =
    HumanReadDocuments.requests
        {
            Basis = Inputs.workBasis
            NextCursor = Some "request-next"
            Items =
                [
                    {
                        Principal = principal
                        FirstSeen = Inputs.workBasis.ObservedAt
                        LastSeen = Inputs.workBasis.ObservedAt
                        Claims =
                            RequesterClaims.admit
                                (name |> Option.map PartyName.format)
                                (Some "fallback input hint")
                                (Some "contact@example.test")
                    }
                ]
        }

[<Tests>]
let tests =
    testList
        "access request human controls"
        [
            testCase
                "authorized request principal and unverified editable hints remain separate from saved captions"
                (fun () ->
                    let original = document (Some(Inputs.name "preferred input hint"))

                    let handle =
                        match original.Context with
                        | InteractionContext.AccessRequests value ->
                            value.Requests |> List.exactlyOne
                        | _ -> failtest "Request identity handles required"

                    Expect.equal
                        handle.Principal
                        principal
                        "Identity can be submitted without parsing displayed cells"

                    Expect.equal
                        handle.ProfileSeed
                        {
                            DisplayName = "preferred input hint"
                            Contact = Some "contact@example.test"
                        }
                        "Name hint precedes username; contact hint stays authored input"

                    let fallback = document None

                    match fallback.Context with
                    | InteractionContext.AccessRequests value ->
                        Expect.equal
                            value.Requests.Head.ProfileSeed.DisplayName
                            "fallback input hint"
                            "Explicit core fallback for missing name hint"
                    | _ -> failtest "Typed controls required"

                    let caption (region: Region) =
                        match region.Content with
                        | RegionContent.Table table when region.Role = RegionRole.Results ->
                            let rows =
                                table.Rows
                                |> List.map (fun row ->
                                    { row with
                                        Values =
                                            table.Columns
                                            |> List.map (fun _ ->
                                                PresentedValue.Text "opaque request caption Ω")
                                    })

                            { region with
                                Content = RegionContent.Table { table with Rows = rows }
                            }
                        | _ -> region

                    let opaque =
                        { original with
                            Screen =
                                { original.Screen with
                                    Regions = original.Screen.Regions |> List.map caption
                                }
                        }

                    Expect.equal
                        opaque.Context
                        original.Context
                        "Read-only presentation never supplies editable values"

                    let bytes = Codec.encode HumanDocumentCodecs.document opaque

                    Expect.isOk
                        (Codec.decode 65536 HumanDocumentCodecs.document bytes)
                        "Opaque labels and typed identities are independently renderable"

                    Expect.equal
                        (HumanDocument.nextCursor opaque)
                        (Some "request-next")
                        "Request paging remains supported")
            testCase
                "request identities and seeds cannot outlive their rows or current disclosure"
                (fun () ->
                    let bytes = Codec.encode HumanDocumentCodecs.document (document None)

                    let mutate edit =
                        let node = JsonNode.Parse(Encoding.UTF8.GetString bytes) |> nonNull
                        edit node
                        Encoding.UTF8.GetBytes(node.ToJsonString())

                    let handles node =
                        let context = (node: JsonNode)["context"] |> nonNull
                        (context["requests"] |> nonNull).AsArray()

                    let invalid =
                        [
                            mutate (fun node -> (handles node).Clear())
                            mutate (fun node ->
                                let items = handles node in
                                items.Add((items[0] |> nonNull).DeepClone()))
                            mutate (fun node ->
                                node["disclosure"] <-
                                    System.Text.Json.Nodes.JsonValue.Create "WITHHELD")
                        ]

                    for raw in invalid do
                        Expect.isError
                            (Codec.validate 65536 HumanDocumentCodecs.document raw)
                            "Nonempty request controls require exact rows and current disclosure")
        ]
