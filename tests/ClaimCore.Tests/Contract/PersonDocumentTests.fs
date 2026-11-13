module ClaimCore.Tests.PersonDocumentTests

open System
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private principal =
    PrincipalKey.human "https://issuer.example/" "person-control"
    |> Result.defaultWith (string >> failtest)

let private actorId =
    CaseFixtures.actorId (Guid.Parse "00000000-0000-4000-8000-000000000001")

let private profile =
    {
        ActorProfileInput.DisplayName = "Editable seed"
        Contact = Some "contact@example.test"
    }

let private actor =
    {
        Actor.ActorId = actorId
        Principal = principal
        Enabled = true
        Profile = Some(ActorProfile.parse profile |> Result.defaultWith (string >> failtest))
    }

let private document offers seed =
    HumanReadDocuments.grants
        {
            Basis = Inputs.workBasis
            Actor = ActorReviewItem.ofActor [] actor
            Offers = offers
            ProfileSeed = seed
            Items = []
            NextCursor = Some "person-next"
        }

[<Tests>]
let tests =
    testList
        "person human controls"
        [
            testCase
                "current policy advertises existing-account tasks only and maintenance removes them"
                (fun () ->
                    let viewer roles =
                        { AccessView.empty with
                            Actor = Some actor
                            Installation = roles
                        }

                    let offers roles maintenance =
                        Advice.actorOffers
                            principal
                            (viewer roles)
                            (RegisterMaintenance.create maintenance)

                    Expect.equal
                        (offers [ Role.Owner ] false)
                        [
                            PeopleActKind.Grant
                            PeopleActKind.Revoke
                            PeopleActKind.Enable
                            PeopleActKind.Disable
                            PeopleActKind.SetProfile
                            PeopleActKind.EraseProfile
                        ]
                        "Literal existing-account action inventory"

                    Expect.isEmpty
                        (offers [ Role.Owner ] true)
                        "Maintenance suppresses input controls"

                    Expect.isEmpty
                        (offers [ Role.Auditor ] false)
                        "Read people confers no mutation controls")
            testCase
                "person target and editable profile remain separate from saved display"
                (fun () ->
                    let value = document [ PeopleActKind.SetProfile ] (Some profile)

                    match value.Context with
                    | InteractionContext.Person control ->
                        Expect.equal control.ActorId actorId "Typed target"
                        Expect.equal control.Principal principal "No caption parsing"
                        Expect.equal control.ProfileSeed (Some profile) "Permitted input seed"

                        Expect.equal
                            control.Tasks
                            [ TaskId.Operation(ActKind.People PeopleActKind.SetProfile) ]
                            "Core task"
                    | _ -> failtest "Person context required"

                    Expect.equal
                        (HumanDocument.nextCursor value)
                        (Some "person-next")
                        "Page cursor"

                    Expect.isOk
                        (Codec.encode HumanDocumentCodecs.document value
                         |> Codec.decode 65536 HumanDocumentCodecs.document)
                        "Actual boundary"

                    Expect.isOk
                        (document [] None
                         |> Codec.encode HumanDocumentCodecs.document
                         |> Codec.decode 65536 HumanDocumentCodecs.document)
                        "Read-only view"

                    let bytes = Codec.encode HumanDocumentCodecs.document value

                    let nonNull value =
                        value
                        |> Option.ofObj
                        |> Option.defaultWith (fun () -> failtest "Missing node")

                    for change in
                        [
                            (fun (node: JsonNode) ->
                                (nonNull node["context"])["tasks"] <- JsonArray())
                            (fun node ->
                                (nonNull node["context"])["tasks"] <-
                                    JsonArray(
                                        System.Text.Json.Nodes.JsonValue.Create "people.register"
                                    ))
                            (fun node ->
                                node["disclosure"] <-
                                    System.Text.Json.Nodes.JsonValue.Create "WITHHELD")
                        ] do
                        let node =
                            JsonNode.Parse(System.Text.Encoding.UTF8.GetString bytes) |> nonNull

                        change node

                        Expect.isError
                            (System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())
                             |> Codec.decode 65536 HumanDocumentCodecs.document)
                            "Input permission/disclosure binding")
            testCase
                "revocation and responsibility destinations remain typed controls separate from captions"
                (fun () ->
                    let parent = ResponseExamples.caseRead.Snapshot.CaseId

                    let item =
                        {
                            GrantReadModel.Role = Role.CaseReader
                            Scope =
                                GrantScope.Case(
                                    parent,
                                    ResponseExamples.caseRead.Snapshot.Reference
                                )
                            GrantedAt = Inputs.workBasis.ObservedAt
                            GrantedBy = ResponseExamples.caseRead.RecordedBy
                        }

                    let read offers =
                        HumanReadDocuments.grants
                            {
                                Basis = Inputs.workBasis
                                Actor =
                                    { ActorReviewItem.ofActor [] actor with
                                        ResponsibleCases =
                                            Some
                                                {
                                                    Count = 0L
                                                    ResponsibleActor = actorId
                                                }
                                    }
                                Offers = offers
                                ProfileSeed = None
                                Items = [ item ]
                                NextCursor = None
                            }

                    let value = read [ PeopleActKind.Revoke ]

                    match value.Context with
                    | InteractionContext.Person controls ->
                        let selected = List.exactlyOne controls.Revocations

                        Expect.equal
                            selected.Key
                            ("CASE_READER:" + CaseId.format parent)
                            "Stable grant row"

                        Expect.equal selected.Role Role.CaseReader "Selected role"

                        Expect.equal
                            selected.Scope
                            (GrantSelectionScope.Case parent)
                            "Case identity without a copied reference"

                        let route =
                            controls.ResponsibleCases
                            |> Option.defaultWith (fun () ->
                                failtest "Navigation required even for known zero")

                        Expect.equal route.ResponsibleActor actorId "Selected account"

                        Expect.equal
                            (route.View, route.Lifecycle, route.Status)
                            (CaseView.All, Lifecycle.Active, CaseStatus.Opened)
                            "Core-owned active responsibility query"
                    | _ -> failtest "Person controls required"

                    match (read []).Context with
                    | InteractionContext.Person controls ->
                        Expect.isEmpty
                            controls.Revocations
                            "No command handles without revoke permission"
                    | _ -> failtest "Person controls required"

                    let bytes = Codec.encode HumanDocumentCodecs.document value

                    Expect.isOk
                        (Codec.decode 65536 HumanDocumentCodecs.document bytes)
                        "Actual native boundary"

                    for change in
                        [
                            (fun (context: JsonNode) -> context["revocations"] <- JsonArray())
                            (fun context -> context["tasks"] <- JsonArray())
                            (fun context ->
                                (nonNull context["responsibleCases"])["responsibleActor"] <-
                                    JsonValue.Create "00000000-0000-4000-8000-000000000099")
                        ] do
                        let node =
                            JsonNode.Parse(System.Text.Encoding.UTF8.GetString bytes) |> nonNull

                        change (nonNull node["context"])

                        Expect.isError
                            (System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())
                             |> Codec.decode 65536 HumanDocumentCodecs.document)
                            "Unbound or unoffered controls refused")
        ]
