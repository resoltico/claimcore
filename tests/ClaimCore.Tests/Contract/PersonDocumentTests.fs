module ClaimCore.Tests.PersonDocumentTests

open System
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
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
        DigestEmail = None
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
            Revision = Revision.first
            Controls =
                offers
                |> List.map (fun task ->
                    Inputs.offeredTask
                        (TaskId.token task)
                        (Binding.People(actorId, Revision.first))
                        (if task = TaskId.ofLiteral "people.setProfile" then
                             Map.ofList
                                 [
                                     TaskInputId.Target InputTarget.ProfileDisplayName,
                                     InputValue.Text profile.DisplayName
                                     TaskInputId.Target InputTarget.ProfileContact,
                                     InputValue.Text profile.Contact.Value
                                 ]
                         else
                             Map.empty))
            Basis = DomainInputs.workBasis
            Actor = ActorReviewItem.ofActor [] actor
            Offers = offers
            ProfileSeed = seed
            CanTerminateSessions = false
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
                            (RegisterMaintenance.create
                                (if maintenance then 1L else 0L)
                                (if maintenance then
                                     Some(System.Guid.Parse "10000000-0000-4000-8000-000000000099")
                                 else
                                     None))

                    Expect.equal
                        (offers [ Role.Owner ] false)
                        [
                            (TaskId.ofLiteral "people.grant")
                            (TaskId.ofLiteral "people.revoke")
                            (TaskId.ofLiteral "people.enable")
                            (TaskId.ofLiteral "people.disable")
                            (TaskId.ofLiteral "people.setProfile")
                            (TaskId.ofLiteral "people.changeSignInIdentity")
                            (TaskId.ofLiteral "people.eraseProfile")
                        ]
                        "Literal existing-account action inventory"

                    Expect.isEmpty
                        (offers [ Role.Owner ] true)
                        "Maintenance suppresses input controls"

                    Expect.isEmpty
                        (offers [ Role.Auditor ] false)
                        "Read people confers no mutation controls")
            testCase "person actions carry the target and permitted profile seeds" (fun () ->
                let value = document [ (TaskId.ofLiteral "people.setProfile") ] (Some profile)
                Expect.equal value.Subject (DocumentSubject.Person actorId) "Typed target"
                let action = List.exactlyOne value.Actions

                Expect.equal action.Task (TaskId.ofLiteral "people.setProfile") "Core task"

                Expect.equal
                    action.Binding
                    (Some(Binding.People(actorId, Revision.first)))
                    "Selected account"

                Expect.isFalse action.Seeds.IsEmpty "Permitted profile seed"
                Expect.equal value.NextCursor (Some "person-next") "Page cursor"
                Expect.isEmpty (document [] None).Actions "Read-only person"

                Expect.isOk
                    (Codec.encode HumanDocumentCodecs.document value
                     |> Codec.decode 65536 HumanDocumentCodecs.document)
                    "Boundary")
            testCase
                "revocation controls belong to granted rows and retain permission boundaries"
                (fun () ->
                    let parent = ResponseExamples.snapshot.CaseId

                    let grant =
                        {
                            GrantReadModel.Controls = []
                            Role = Role.CaseReader
                            Scope = GrantScope.Case(parent, ResponseExamples.snapshot.Reference)
                            GrantedAt = DomainInputs.workBasis.ObservedAt
                            GrantedBy = AuthorityRef.Actor(ResponseExamples.caseRead.RecordedBy)
                        }

                    let read offers =
                        let binding = Binding.People(actorId, Revision.first)

                        let seeds =
                            Map.ofList
                                [
                                    TaskInputId.Target InputTarget.Role,
                                    InputValue.Text "CASE_READER"
                                    TaskInputId.Scope, InputValue.Text "CASE"
                                    TaskInputId.Target(
                                        InputTarget.Field BusinessField.CaseReference
                                    ),
                                    InputValue.Text(
                                        CaseReference.format ResponseExamples.snapshot.Reference
                                    )
                                ]

                        let rowOffers =
                            if List.contains (TaskId.ofLiteral "people.revoke") offers then
                                match Inputs.offeredTask "people.revoke" binding seeds with
                                | TaskOffer.Available(task, binding, available) ->
                                    [
                                        TaskOffer.Available(
                                            task,
                                            binding,
                                            { available with Editable = [] }
                                        )
                                    ]
                                | _ -> failtest "Available fixture required"
                            else
                                []

                        HumanReadDocuments.grants
                            {
                                Revision = Revision.first
                                Basis = DomainInputs.workBasis
                                Actor = ActorReviewItem.ofActor [] actor
                                Offers = offers
                                Controls =
                                    offers
                                    |> List.map (fun task ->
                                        Inputs.offeredTask (TaskId.token task) binding Map.empty)
                                ProfileSeed = None
                                CanTerminateSessions = false
                                Items = [ { grant with Controls = rowOffers } ]
                                NextCursor = None
                            }

                    let rows document =
                        document.Screen.Regions
                        |> List.collect (fun region ->
                            match region.Content with
                            | RegionContent.Table table -> table.Rows
                            | _ -> [])

                    let value = read [ (TaskId.ofLiteral "people.revoke") ]
                    let general = List.exactlyOne value.Actions

                    Expect.equal
                        general.Binding
                        (Some(Binding.People(actorId, Revision.first)))
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
                        (Some(Binding.People(actorId, Revision.first)))
                        "Immutable selected account"

                    Expect.equal
                        control.Seeds
                        (Map.ofList
                            [
                                TaskInputId.Target InputTarget.Role, InputValue.Text "CASE_READER"
                                TaskInputId.Scope, InputValue.Text "CASE"
                                TaskInputId.Target(InputTarget.Field BusinessField.CaseReference),
                                InputValue.Text(
                                    CaseReference.format ResponseExamples.snapshot.Reference
                                )
                            ])
                        "Fixed role and scope, independent of caption"

                    Expect.isEmpty
                        ((read [] |> rows |> List.exactlyOne).Actions)
                        "Read permission never grants revocation")
        ]
