module ClaimCore.Tests.RequesterControlDocumentTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private actor =
    {
        ActorContext.Issuer = "https://issuer.example/"
        Roles = []
        Self = ResponseExamples.caseRead.RecordedBy
        Maintenance = RegisterMaintenance.initial
        InstallationId = Guid.Parse "90000000-0000-4000-8000-000000000001"
        ActorId = ResponseExamples.caseRead.RecordedBy.ActorId
        Runtime =
            {
                ProductVersion = "synthetic"
                Basis = Inputs.workBasis
            }
        Capability = [ Capability.ReadAccess ]
        View = [ View.People; View.Operations ]
        Offers = []
        HasScopedCases = false
    }

let private document actor =
    HumanReadDocuments.context
        {
            Requester = RequesterState.Active actor
        }

let private control value =
    match value.Context with
    | InteractionContext.Requester c -> c
    | _ -> failtest "Requester controls required"

[<Tests>]
let tests =
    testList
        "requester human controls"
        [
            testCase "people reader navigation comes from advertised query tasks" (fun () ->
                let value = document actor
                let c = control value

                Expect.equal
                    c.Tasks
                    [
                        TaskId.Query QueryTaskId.PeopleList
                        TaskId.Query QueryTaskId.PeopleGrants
                        TaskId.Query QueryTaskId.PeopleHistory
                    ]
                    "Literal read task inventory"

                Expect.equal c.InstallationId (Some actor.InstallationId) "Owner installation"
                Expect.equal c.ActorId (Some actor.ActorId) "Owner actor"

                Expect.isNone c.PeopleIssuerSeed "Read people offers no editable people seed"

                Expect.isOk
                    (Codec.encode HumanDocumentCodecs.document value
                     |> Codec.decode 65536 HumanDocumentCodecs.document)
                    "Native reader document")
            testCase
                "maintenance withdraws mutation tasks and issuer seeds while Core supplies the notice"
                (fun () ->
                    let manager =
                        { actor with
                            Capability = [ Capability.ReadAccess; Capability.ManageAccess ]
                            Offers =
                                [
                                    InstallationOffer.PeopleAct PeopleActKind.Register
                                    InstallationOffer.PeopleAct PeopleActKind.Admit
                                ]
                        }

                    let openValue = document manager

                    Expect.equal
                        (control openValue).PeopleIssuerSeed
                        (Some "https://issuer.example/")
                        "Permitted editable issuer default"

                    Expect.contains
                        (control openValue).Tasks
                        (TaskId.Query QueryTaskId.PeopleRequests)
                        "Request review is independently offered"

                    let paused =
                        document
                            { manager with
                                Maintenance = RegisterMaintenance.create true
                            }

                    Expect.isNone
                        (control paused).PeopleIssuerSeed
                        "No seed without an offered mutation"

                    Expect.isFalse
                        ((control paused).Tasks
                         |> List.exists (function
                             | TaskId.Operation _ -> true
                             | _ -> false))
                        "Maintenance closes ledger mutations"

                    Expect.isTrue
                        (paused.Screen.Regions
                         |> List.exists (fun region ->
                             match region.Content with
                             | RegionContent.Notice notice ->
                                 notice.MessageId = "diagnostic.REGISTER_MAINTENANCE"
                             | _ -> false))
                        "Core owns maintenance presentation"

                    Expect.isOk
                        (Codec.encode HumanDocumentCodecs.document paused
                         |> Codec.decode 65536 HumanDocumentCodecs.document)
                        "Native paused document")
            testCase
                "requester structural boundary refuses missing active identity, missing basis and unoffered seeds"
                (fun () ->
                    let bytes = Codec.encode HumanDocumentCodecs.document (document actor)

                    for change in
                        [
                            (fun (node: JsonNode) -> (nonNull node["context"])["actorId"] <- null)
                            (fun node ->
                                node["basis"] <- JsonNode.Parse("{\"kind\":\"UNAVAILABLE\"}"))
                            (fun node ->
                                (nonNull node["context"])["peopleIssuerSeed"] <-
                                    JsonValue.Create "https://issuer.example/")
                        ] do
                        let node = JsonNode.Parse(Encoding.UTF8.GetString bytes) |> nonNull
                        change node

                        Expect.isError
                            (Encoding.UTF8.GetBytes(node.ToJsonString())
                             |> Codec.decode 65536 HumanDocumentCodecs.document)
                            "Control binding refused")
        ]
