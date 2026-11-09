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
                        { AuthorityView.empty with
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
                            AuthorityKind.Grant
                            AuthorityKind.Revoke
                            AuthorityKind.Enable
                            AuthorityKind.Disable
                            AuthorityKind.SetProfile
                            AuthorityKind.EraseProfile
                        ]
                        "Literal existing-account action inventory"

                    Expect.isEmpty
                        (offers [ Role.Owner ] true)
                        "Maintenance suppresses input controls"

                    Expect.isEmpty
                        (offers [ Role.Auditor ] false)
                        "Read authority confers no mutation controls")
            testCase
                "person target and editable profile remain separate from saved display"
                (fun () ->
                    let value = document [ AuthorityKind.SetProfile ] (Some profile)

                    match value.Context with
                    | InteractionContext.Person control ->
                        Expect.equal control.ActorId actorId "Typed target"
                        Expect.equal control.Principal principal "No caption parsing"
                        Expect.equal control.ProfileSeed (Some profile) "Permitted input seed"

                        Expect.equal
                            control.Tasks
                            [ TaskId.Operation(ActKind.Authority AuthorityKind.SetProfile) ]
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
                                        System.Text.Json.Nodes.JsonValue.Create
                                            "AUTHORITY:REGISTER"
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
        ]
