module ClaimCore.TestSupport.PeopleDirectoryContract

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private required result =
    result
    |> Result.defaultWith (fun error -> failtestf "Directory store fault: %A" error)

let private successful =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Directory outcome: %A" other

let private principal subject =
    PrincipalKey.human "https://issuer.example/" subject |> required

let private register owner operation principal =
    Service.execute
        owner
        (OperationInput.Authority(
            Inputs.authorityRequest
                (id operation)
                principal
                (AuthorityAct.Register Inputs.actorProfile)
        ))
        token

let private directory owner principal =
    task {
        let! page =
            Service.actors
                owner
                {
                    Role = None
                    Text = None
                    AfterCursor = None
                    Limit = 50
                }
                token

        return (successful page).Items |> List.find (fun item -> item.Principal = principal)
    }

let private grantRole owner operation principal role scope active =
    task {
        let! result =
            grant owner (id operation) principal { Role = role; Scope = scope } active

        acceptedAuthority result
    }

let internal summaries fixture =
    task {
        let! owner = caseWorker fixture

        let! a =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request 1001 "PEOPLE-A" 0L (AuthoredCaseAct.Open registration)
                ))
                token

        let a = accepted a |> caseReceipt

        let! b =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request 1002 "PEOPLE-B" 0L (AuthoredCaseAct.Open registration)
                ))
                token

        let b = accepted b |> caseReceipt
        let person = principal "directory-person"
        let! created = register owner 1010 person

        let personId =
            (accepted created).Subject
            |> function
                | ReceiptSubject.Authority value ->
                    AuthorityTargetDisclosure.actorId value.TargetActor
                | _ -> failtest "Expected actor"

        let! noRoles = directory owner person

        Expect.equal
            noRoles.State
            AccountState.NoRoles
            "Registered enabled account has no assignments"

        Expect.isEmpty noRoles.Roles "Zero assignments have no fabricated role chip"

        Expect.equal
            noRoles.Actor.Availability
            ActorAvailability.Available
            "Assignment eligibility is a distinct actor-reference concern"

        do! grantRole owner 1011 person Role.CaseReader GrantTarget.Installation true
        do! grantRole owner 1012 person Role.CaseReader (GrantTarget.CaseId a.CaseId) true
        do! grantRole owner 1013 person Role.CaseReader (GrantTarget.CaseId b.CaseId) true
        do! grantRole owner 1014 person Role.Auditor (GrantTarget.CaseId b.CaseId) true
        let! assigned = directory owner person

        Expect.equal
            assigned.State
            AccountState.Active
            "Enabled with assigned roles is explicit core state"

        Expect.equal
            assigned.Roles
            [
                {
                    Role = Role.CaseReader
                    Installation = true
                    CaseCount = 2L
                }
                {
                    Role = Role.Auditor
                    Installation = false
                    CaseCount = 1L
                }
            ]
            "One bounded summary combines installation and scoped assignments per role"

        let! selected =
            Service.grants
                owner
                {
                    ActorId = ActorId.value personId
                    AfterCursor = None
                    Limit = 1
                }
                token

        let selected = successful selected

        Expect.equal
            selected.Actor
            assigned
            "Selected-person identity/state/summaries use the same core directory model"

        Expect.equal selected.Items.Length 1 "Full grants remain independently paged"

        Expect.isSome
            selected.NextCursor
            "Scope detail is not flattened into an unbounded actor row"

        let! disabled =
            Service.execute
                owner
                (OperationInput.Authority(
                    Inputs.authorityRequest (id 1015) person AuthorityAct.Disable
                ))
                token

        acceptedAuthority disabled
        let! blocked = directory owner person

        Expect.equal
            blocked.State
            AccountState.Disabled
            "Disabled state wins while assignments remain visible"

        Expect.equal
            blocked.Roles
            assigned.Roles
            "Disabling never hides assigned role facts from authority review"

        let! preview =
            Service.preview
                owner
                (OperationInput.Authority(
                    Inputs.authorityRequest (id 1016) person AuthorityAct.Enable
                ))
                token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed(OperationReview.Authority review)) ->
            let target = review.Target |> Option.get

            Expect.equal
                target.State
                AccountState.Disabled
                "Authority review carries the actual target assignment state"

            Expect.equal
                target.Roles
                assigned.Roles
                "Authority review sees all target roles, not only its selected scope"
        | other -> failtestf "Disabled target remains reviewable: %A" other

        do! grantRole owner 1017 person Role.CaseReader (GrantTarget.CaseId a.CaseId) false
        let! changed = directory owner person

        Expect.equal
            (changed.Roles |> List.find (fun value -> value.Role = Role.CaseReader)).CaseCount
            1L
            "Fresh directory snapshots recompute scoped counts"
    }

let internal corruptAssignment fixture =
    task {
        let! owner = caseWorker fixture

        let! opened =
            Service.execute
                owner
                (Inputs.caseOperation (
                    request 1030 "PEOPLE-CORRUPT" 0L (AuthoredCaseAct.Open registration)
                ))
                token

        let case = accepted opened |> caseReceipt

        let service =
            PrincipalKey.service "https://issuer.example/" "directory-service" |> required

        let! registered = register owner 1031 service

        let actorId =
            (accepted registered).Subject
            |> function
                | ReceiptSubject.Authority value ->
                    AuthorityTargetDisclosure.actorId value.TargetActor
                | _ -> failtest "Expected actor"

        let core = core fixture service
        let! before = Service.context core () token

        match successful before with
        | { Requester = RequesterState.NoRoles _ } -> ()
        | other -> failtestf "Expected roleless service: %A" other

        let! injected =
            fixture.Store.Write
                {
                    CancellationToken = token
                    Work =
                        fun tx ->
                            task {
                                do!
                                    tx.ProjectAuthority(
                                        AuthorityProjection.AddGrant(
                                            actorId,
                                            Role.DataSteward,
                                            Some case.CaseId
                                        )
                                    )

                                return Verdict.Proceed()
                            }
                }

        Expect.equal
            injected
            (Ok(Verdict.Proceed()))
            "The negative control really creates an inadmissible recognized assignment"

        let! after = Service.context core () token

        match successful after with
        | {
              Requester = RequesterState.Active context
          } ->
            Expect.equal
                context.Roles
                [
                    {
                        Role = Role.DataSteward
                        Installation = false
                        CaseCount = 1L
                    }
                ]
                "Own assignment labels retain recognized invalid roles without granting their rights"

            Expect.isEmpty
                context.Capability
                "Assignment display does not manufacture authorization"

            Expect.isFalse
                context.HasScopedCases
                "Filtered scope visibility still vetoes the inadmissible service role"
        | other -> failtestf "Assigned state is distinct from eligibility: %A" other

        let! denied =
            Service.get
                core
                {
                    Target = CaseSelector.Reference "PEOPLE-CORRUPT"
                }
                token

        Expect.equal
            denied
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Existing loaded-scope security veto is unchanged"

        let! hidden =
            Service.list core (Inputs.caseListPage { AfterCursor = None; Limit = 50 }) token

        Expect.equal
            hidden
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Assignment state does not add case-list visibility"

        let! person = directory owner service

        Expect.equal
            person.State
            AccountState.Active
            "Directory matches caller context assignment state"

        Expect.equal
            person.Roles
            [
                {
                    Role = Role.DataSteward
                    Installation = false
                    CaseCount = 1L
                }
            ]
            "Recognized invalid assignments remain reviewable rather than silently filtered"

        let! filtered =
            Service.actors
                owner
                {
                    ActorListRequest.Text = None
                    Role = Some Role.DataSteward
                    AfterCursor = None
                    Limit = 50
                }
                token

        Expect.equal
            ((successful filtered).Items |> List.map _.Principal)
            [ service ]
            "Typed role filter retains recognized raw assignments without granting protected rights"

        let! grants =
            Service.grants
                owner
                {
                    ActorId = ActorId.value actorId
                    AfterCursor = None
                    Limit = 50
                }
                token

        Expect.equal
            (successful grants).Actor
            person
            "Grant review retains the same actor state and bounded summary"

        do! grantRole owner 1032 service Role.DataSteward (GrantTarget.CaseId case.CaseId) false
        let! repaired = directory owner service

        Expect.equal
            repaired.State
            AccountState.NoRoles
            "Authorized removal restores roleless assignment state"

        Expect.isEmpty
            repaired.Roles
            "The invalid assignment remains removable without a new permission gate"
    }

let internal maximumRoles fixture =
    task {
        let! owner = bootstrap fixture
        let target = principal "seven-directory-roles"
        let! registered = register owner 1050 target
        acceptedAuthority registered

        let roles =
            [
                Role.Owner
                Role.CaseReader
                Role.CaseRegistrar
                Role.CaseHandler
                Role.PaymentRecorder
                Role.DataSteward
                Role.Auditor
            ]

        for index, role in List.indexed roles do
            do! grantRole owner (1051 + index) target role GrantTarget.Installation true

        let! person = directory owner target
        let! own = Service.context (core fixture target) () token

        match successful own with
        | {
              Requester = RequesterState.Active context
          } ->
            Expect.equal
                context.Roles
                person.Roles
                "Own context and authorized directory use the same complete ordered role summary semantics"
        | other -> failtestf "Assigned target needs active own context: %A" other

        Expect.equal person.Roles.Length 7 "The complete vocabulary has at most7 per-role summaries"

        Expect.equal
            (person.Roles |> List.map _.Role)
            roles
            "Core vocabulary order is deterministic across storage adapters"

        Expect.isTrue
            (person.Roles
             |> List.forall (fun value -> value.Installation && value.CaseCount = 0L))
            "Installation flags are not confused with case counts"

        Expect.equal
            person.State
            AccountState.Active
            "Role assignment state is computed once in the core"
    }
