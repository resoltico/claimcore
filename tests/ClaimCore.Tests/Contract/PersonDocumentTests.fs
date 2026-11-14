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
            testCase "person actions carry the target and permitted profile seeds" (fun () ->
                let value = document [ PeopleActKind.SetProfile ] (Some profile)
                Expect.equal value.Subject (DocumentSubject.Person actorId) "Typed target"
                let action = List.exactlyOne value.Actions

                Expect.equal
                    action.Task
                    (TaskId.Operation(ActKind.People PeopleActKind.SetProfile))
                    "Core task"

                Expect.equal
                    action.Binding
                    (OperationBinding.People(Some(ActorId.value actorId)))
                    "Selected account"

                Expect.isFalse action.Seeds.IsEmpty "Permitted profile seed"
                Expect.equal value.NextCursor (Some "person-next") "Page cursor"
                Expect.isEmpty (document [] None).Actions "Read-only person"

                Expect.isOk
                    (Codec.encode HumanDocumentCodecs.document value
                     |> Codec.decode 65536 HumanDocumentCodecs.document)
                    "Boundary")
            testCase "Core chooses the primary person action" (fun () ->
                let value =
                    document
                        [ PeopleActKind.Disable; PeopleActKind.Enable; PeopleActKind.Grant ]
                        None

                Expect.equal
                    (value.Actions |> List.filter _.Primary |> List.map _.Task)
                    [ TaskId.Operation(ActKind.People PeopleActKind.Grant) ]
                    "Grant has the Core preference")
            testCase
                "revocation controls belong to granted rows and retain permission boundaries"
                (fun () ->
                    let parent = ResponseExamples.snapshot.CaseId

                    let grant =
                        {
                            GrantReadModel.Role = Role.CaseReader
                            Scope = GrantScope.Case(parent, ResponseExamples.snapshot.Reference)
                            GrantedAt = Inputs.workBasis.ObservedAt
                            GrantedBy = ResponseExamples.caseRead.RecordedBy
                        }

                    let read offers =
                        HumanReadDocuments.grants
                            {
                                Basis = Inputs.workBasis
                                Actor = ActorReviewItem.ofActor [] actor
                                Offers = offers
                                ProfileSeed = None
                                Items = [ grant ]
                                NextCursor = None
                            }

                    let rows document =
                        document.Screen.Regions
                        |> List.collect (fun region ->
                            match region.Content with
                            | RegionContent.Table table -> table.Rows
                            | _ -> [])

                    let value = read [ PeopleActKind.Revoke ]
                    let general = List.exactlyOne value.Actions

                    Expect.equal
                        general.Binding
                        (OperationBinding.People(Some(ActorId.value actorId)))
                        "General revocation binds the known account"

                    Expect.contains
                        general.Editable
                        (TaskInputId.Target InputTarget.Role)
                        "General revocation lets the user choose its role"

                    let row = rows value |> List.exactlyOne

                    Expect.equal
                        row.Link
                        (Some(Link.caseById parent))
                        "Case identity travels on the grant row"

                    let control = List.exactlyOne row.Actions

                    Expect.equal
                        control.Binding
                        (OperationBinding.People(Some(ActorId.value actorId)))
                        "Immutable selected account"

                    Expect.equal
                        control.Seeds
                        (Map.ofList
                            [
                                TaskInputId.Target InputTarget.Role,
                                SubmissionValue.Text "CASE_READER"
                                TaskInputId.Scope, SubmissionValue.Text "CASE"
                                TaskInputId.Target(InputTarget.Field BusinessField.CaseReference),
                                SubmissionValue.Text(
                                    CaseReference.format ResponseExamples.snapshot.Reference
                                )
                            ])
                        "Fixed role and scope, independent of caption"

                    Expect.isEmpty
                        ((read [] |> rows |> List.exactlyOne).Actions)
                        "Read permission never grants revocation")
        ]
