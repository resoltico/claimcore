module internal ClaimCore.TestSupport.ActorResponsibilityScenarios

open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected successful scoped read: %A" other

let private principal name create =
    create "https://issuer.example/" name |> Result.defaultWith (string >> failtest)

let private execute services input =
    task {
        let! reply = Service.executeSubmission services input token
        accepted reply |> ignore
    }

let private directory services =
    task {
        let! page =
            Service.actors
                services
                {
                    Role = None
                    Text = None
                    AfterCursor = None
                    Limit = 50
                }
                token

        return read page
    }

let private cases services filter =
    task {
        let! result =
            Service.list
                services
                {
                    Page = { AfterCursor = None; Limit = 50 }
                    Filter = filter
                }
                token

        return read result
    }

let run (fixture: Fixture) (corrupt: ActorId -> CaseId -> Task<unit>) =
    task {
        let! admin = caseWorker fixture
        let serial = ref 73000

        let operation () =
            serial.Value <- serial.Value + 1
            id fixture serial.Value

        let register name kind =
            task {
                let target = principal name kind
                let registration = operation ()

                do!
                    execute
                        admin
                        (Submissions.create
                            "people.register"
                            registration
                            (Binding.PeopleNew Revision.zero)
                            (Submissions.principal target
                             |> Map.fold
                                 (fun values key value -> Map.add key value values)
                                 (Submissions.profile { DisplayName = name; Contact = None })))

                return target, ActorId.ofOperationId (CaseFixtures.operationId registration)
            }

        let addRole target role scope =
            task {
                let! reply = grant admin (operation ()) target { Role = role; Scope = scope } true
                acceptedPeople reply
            }

        let! staff, staffId = register "assigned-person" PrincipalKey.human
        do! addRole staff Role.CaseHandler GrantTarget.Installation
        let! zero, _ = register "no-assignments" PrincipalKey.human
        let! ownerOnly, _ = register "owner-only-viewer" PrincipalKey.human
        do! addRole ownerOnly Role.Owner GrantTarget.Installation
        let! scoped, _ = register "scoped-viewer" PrincipalKey.human
        do! addRole scoped Role.Owner GrantTarget.Installation
        let! globalViewer, _ = register "global-viewer" PrincipalKey.human
        do! addRole globalViewer Role.Owner GrantTarget.Installation
        do! addRole globalViewer Role.CaseReader GrantTarget.Installation
        let! service, serviceId = register "service-viewer" PrincipalKey.service
        do! addRole service Role.Auditor GrantTarget.Installation
        do! addRole admin.Principal Role.DataSteward GrantTarget.Installation

        let created = ResizeArray<CaseId>()

        for reference in [ "RESP-A"; "RESP-B"; "RESP-C"; "RESP-D" ] do
            let opening = operation ()

            do!
                execute
                    admin
                    (Submissions.create
                        "case.open"
                        opening
                        (Binding.CaseNew Revision.zero)
                        (Submissions.fields (RegistrationInput.values DomainInputs.registration)
                         |> Map.add
                             (Submissions.field BusinessField.CaseReference)
                             (SubmissionValue.Text reference)))

            let caseId = CaseId.ofOperationId (CaseFixtures.operationId opening)
            created.Add caseId

            do!
                execute
                    admin
                    (Submissions.create
                        "work.update"
                        (operation ())
                        (Binding.Work(CaseSelector.Id caseId, Revision.zero))
                        (Map.ofList
                            [
                                Submissions.field BusinessField.ResponsibleActor,
                                SubmissionValue.Text(ActorId.format staffId)
                            ]))

        do!
            execute
                admin
                (Submissions.create
                    "case.close"
                    (operation ())
                    (Binding.Case(CaseSelector.Reference "RESP-B", Revision.first))
                    Map.empty)

        do!
            execute
                admin
                (Submissions.create
                    "case.void"
                    (operation ())
                    (Binding.Case(CaseSelector.Reference "RESP-C", Revision.first))
                    (Map.ofList
                        [
                            Submissions.input InputTarget.Reason,
                            SubmissionValue.Text "Incorrect case"
                        ]))

        do!
            execute
                admin
                (Submissions.create
                    "case.erase"
                    (operation ())
                    (Binding.CaseErase(
                        CaseSelector.Reference "RESP-D",
                        Revision.first,
                        reviewedFamilySequence fixture.Store (CaseSelector.Reference "RESP-D")
                        |> CaseFixtures.sequence
                    ))
                    Map.empty)

        for caseId in [ created[0]; created[1] ] do
            do! addRole scoped Role.CaseReader (GrantTarget.CaseId caseId)

        do! corrupt serviceId created[0]

        let target services name =
            task {
                let! page = directory services
                return page.Items |> List.find (fun item -> item.Principal = name)
            }

        let! hidden = target (core fixture ownerOnly) staff

        Expect.isNone
            hidden.ResponsibleCases
            "An owner with no case-read capability receives no count or link selection"

        Expect.isNone
            (ResponseCodecs.actorSummary.Write hidden
             |> JsonValue.tryMember "responsibleCases")
            "Unavailable is omitted entirely, not null or zero"

        let! ownerGrants =
            Service.grants
                (core fixture ownerOnly)
                {
                    ActorId = ActorId.value staffId
                    AfterCursor = None
                    Limit = 50
                }
                token

        Expect.isNone
            (read ownerGrants).Actor.ResponsibleCases
            "The related grants header has the same omission boundary"

        for viewer, expected, references in
            [ globalViewer, 1L, [ "RESP-A" ]; scoped, 1L, [ "RESP-A" ]; service, 0L, [] ] do
            let services = core fixture viewer
            let! item = target services staff

            let selection =
                item.ResponsibleCases
                |> Option.defaultWith (fun () -> failtest "Readable responsibility is explicit")

            Expect.equal
                selection.Count
                expected
                "The count belongs to the viewer's readable scope, not the target's grants"

            Expect.equal
                selection.View
                CaseView.All
                "The base view intersects the active-open filters"

            Expect.equal
                selection.ResponsibleActor
                staffId
                "The link targets the directory actor, never MINE"

            let filter =
                { CaseListFilter.empty<string> with
                    View = Some selection.View
                    ResponsibleActor = Some selection.ResponsibleActor
                    Lifecycles = [ selection.Lifecycle ]
                    Statuses = [ selection.Status ]
                }

            let! page = cases services filter

            Expect.equal
                (page.Items |> List.map (fun item -> CaseReference.format item.Reference))
                references
                "The core link selection is executable with the identical visibility boundary"

            let! summary =
                Service.caseSummary services (CaseSummaryRequest.ungrouped filter) token

            Expect.equal
                (read summary).Counts.Total
                selection.Count
                "Aggregation and link filtering agree"

            let! zeroItem = target services zero

            Expect.equal
                (zeroItem.ResponsibleCases |> Option.map _.Count)
                (Some 0L)
                "A readable known zero differs from unavailable"

            let! grants =
                Service.grants
                    services
                    {
                        ActorId = ActorId.value staffId
                        AfterCursor = None
                        Limit = 50
                    }
                    token

            Expect.equal
                (read grants).Actor.ResponsibleCases
                item.ResponsibleCases
                "Directory and related grant headers use the same viewer scope"

        let globalServices = core fixture globalViewer

        let baseFilter =
            { CaseListFilter.empty<string> with
                ResponsibleActor = Some staffId
                View = Some CaseView.All
            }

        let! closed =
            cases
                globalServices
                { baseFilter with
                    Statuses = [ CaseStatus.Closed ]
                }

        Expect.equal
            (closed.Items |> List.map (fun item -> CaseReference.format item.Reference))
            [ "RESP-B" ]
            "Explicit status intersects responsibility"

        let! voided =
            cases
                globalServices
                { baseFilter with
                    View = Some CaseView.Voided
                }

        Expect.equal
            (voided.Items |> List.map (fun item -> CaseReference.format item.Reference))
            [ "RESP-C" ]
            "Explicit view intersects responsibility"

        let! incompatible =
            cases
                globalServices
                { baseFilter with
                    Assignment = Some CaseAssignment.Unassigned
                }

        Expect.isEmpty incompatible.Items "Contradictory filters do not override one another"

        let! first =
            Service.list
                globalServices
                {
                    Page = { AfterCursor = None; Limit = 1 }
                    Filter = baseFilter
                }
                token

        let first = read first
        Expect.isSome first.NextCursor "The cursor control has real remaining rows"

        let! changed =
            Service.list
                globalServices
                {
                    Page =
                        {
                            AfterCursor = first.NextCursor
                            Limit = 1
                        }
                    Filter =
                        { baseFilter with
                            ResponsibleActor = None
                        }
                }
                token

        Expect.equal
            changed
            (Outcome.Rejected Refusal.InvalidCursor)
            "Cursor format 7 binds responsibility"

        let! continued =
            Service.list
                globalServices
                {
                    Page =
                        {
                            AfterCursor = first.NextCursor
                            Limit = 1
                        }
                    Filter = baseFilter
                }
                token

        Expect.equal
            ((read continued).Items
             |> List.map (fun item -> CaseReference.format item.Reference))
            [ "RESP-B" ]
            "Unchanged selection continues"

        for services in
            [
                core fixture staff
                core fixture (principal "unknown-viewer" PrincipalKey.human)
            ] do
            let! denied =
                Service.actors
                    services
                    {
                        Role = None
                        Text = None
                        AfterCursor = None
                        Limit = 50
                    }
                    token

            Expect.equal
                denied
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "Case readability never grants directory access"

            let! denied =
                Service.grants
                    services
                    {
                        ActorId = ActorId.value staffId
                        AfterCursor = None
                        Limit = 50
                    }
                    token

            Expect.equal
                denied
                (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
                "A related DTO never bypasses READ_ACCESS"
    }
