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
                Basis = DomainInputs.workBasis
            }
        Capability = [ Capability.ReadAccess ]
        View = [ View.People; View.Operations ]
        Offers = []
        HasScopedCases = false
    }

let private document actor =
    ContextScreen.document
        {
            Requester = RequesterState.Active actor
        }

[<Tests>]
let tests =
    testList
        "requester human controls"
        [
            testCase "requester identity and navigation come from Core" (fun () ->
                let value = document actor

                match value.Subject with
                | DocumentSubject.Requester identity ->
                    Expect.equal identity.InstallationId (Some actor.InstallationId) "Installation"
                    Expect.equal identity.ActorId (Some actor.ActorId) "Account"
                | _ -> failtest "Requester subject required"

                Expect.equal
                    (value.Links |> List.map (_.Link >> Link.endpoint))
                    [ QueryTaskId.PeopleList; QueryTaskId.OperationGet; QueryTaskId.PeopleHistory ]
                    "Advertised destinations"

                Expect.equal
                    (value.Actions |> List.map _.Control)
                    [ ActionControl.Command CommandRequest.TerminateOwnSessions ]
                    "Read-only requester may still end browser sessions"

                Expect.isOk
                    (Codec.encode HumanDocumentCodecs.document value
                     |> Codec.decode 65536 HumanDocumentCodecs.document)
                    "Boundary")
            testCase "maintenance removes authoring seeds and retains its Core notice" (fun () ->
                let manager =
                    { actor with
                        Capability = [ Capability.ReadAccess; Capability.ManageAccess ]
                        Offers =
                            [
                                InstallationOffer.PeopleAct(TaskId.ofLiteral "people.register")
                                InstallationOffer.PeopleAct(TaskId.ofLiteral "people.admit")
                            ]
                    }

                let current = document manager

                let creation =
                    current.Actions
                    |> List.filter (fun action ->
                        match action.Control with
                        | ActionControl.Operation _ -> true
                        | _ -> false)

                Expect.equal creation.Length 2 "Creation controls"

                for action in creation do
                    Expect.equal
                        (Map.tryFind
                            (TaskAffordances.input InputTarget.PrincipalIssuer)
                            action.Seeds)
                        (Some(InputValue.Text "https://issuer.example/"))
                        "Issuer default"

                let paused =
                    document
                        { manager with
                            Maintenance =
                                RegisterMaintenance.create
                                    1L
                                    (Some(System.Guid.Parse "10000000-0000-4000-8000-000000000099"))
                        }

                Expect.equal
                    (paused.Actions |> List.map _.Control)
                    [ ActionControl.Command CommandRequest.TerminateOwnSessions ]
                    "Maintenance retains independent browser-session termination"

                Expect.isTrue
                    (paused.Screen.Regions
                     |> List.exists (fun region ->
                         match region.Content with
                         | RegionContent.Notice notices ->
                             notices
                             |> List.exists (fun notice ->
                                 notice.MessageId = "diagnostic.REGISTER_MAINTENANCE")
                         | _ -> false))
                    "Core notice"

                Expect.isOk
                    (Codec.encode HumanDocumentCodecs.document paused
                     |> Codec.decode 65536 HumanDocumentCodecs.document)
                    "Boundary")
            testCase
                "active requester construction requires identity and a snapshot basis"
                (fun () ->
                    let value = document actor

                    let identity =
                        {
                            RequesterSubject.State = RequesterKind.Active
                            InstallationId = None
                            ActorId = Some actor.ActorId
                        }

                    Expect.throws
                        (fun () ->
                            HumanDocument.create
                                (DocumentSubject.Requester identity)
                                value.Screen
                                []
                                []
                                None
                                value.Basis
                                value.Disclosure
                                None
                            |> ignore)
                        "Missing installation refuses"

                    Expect.throws
                        (fun () ->
                            HumanDocument.create
                                value.Subject
                                value.Screen
                                []
                                []
                                None
                                ObservationBasis.Unavailable
                                value.Disclosure
                                None
                            |> ignore)
                        "Missing active basis refuses")
            testCase "case navigation supplies the existing Pending reminder default" (fun () ->
                let value = document { actor with View = [ View.Cases ] }

                let reminder =
                    value.Links
                    |> List.find (fun link -> Link.endpoint link.Link = QueryTaskId.ReminderList)

                Expect.equal reminder.LabelId "label.reminders" "Core caption"

                match reminder.Link with
                | Link.ReminderList query ->
                    Expect.equal
                        query.View
                        (Some ReminderView.Pending)
                        "First supported reminder view"
                | _ -> failtest "Reminder query required")
            testCase
                "actual owner creation offers construct and round trip a ready requester document"
                (fun () ->
                    let principal =
                        PrincipalKey.human actor.Issuer "owner"
                        |> Result.defaultWith (string >> failtest)

                    let account =
                        {
                            Actor.ActorId = actor.ActorId
                            Principal = principal
                            Enabled = true
                            Profile = None
                        }

                    let access =
                        { AccessView.empty with
                            Actor = Some account
                            Installation = [ Role.Owner; Role.CaseRegistrar ]
                            HasAssignedRoles = true
                        }

                    let offers =
                        ClaimCore.Application.Reads.Advice.installationOffers principal access

                    Expect.equal
                        offers
                        [
                            InstallationOffer.Open
                            InstallationOffer.PeopleAct(TaskId.ofLiteral "people.register")
                            InstallationOffer.PeopleAct(TaskId.ofLiteral "people.admit")
                        ]
                        "Only bindings with no selected existing actor are offered here"

                    let value = document { actor with Offers = offers }

                    Expect.equal
                        value.Actions.Length
                        4
                        "Every real offer and own-session command constructs"

                    Expect.isOk
                        (Codec.encode HumanDocumentCodecs.document value
                         |> Codec.decode 65536 HumanDocumentCodecs.document)
                        "Actual owner context boundary")
        ]
