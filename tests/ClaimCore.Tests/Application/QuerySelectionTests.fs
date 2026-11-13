module ClaimCore.Tests.QuerySelectionTests

open ClaimCore.TestSupport

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Tests.DecisionFixtures


let private peopleAndCursor () =
    let page: PageRequest =
        {
            AfterCursor = Some "malformed"
            Limit = 1
        }

    let ungranted = actor owner ((CaseFixtures.actorId (id 1))) [] []

    let read = ungranted

    Expect.equal
        (Queries.selectCases
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            Inputs.workBasis
            (ClaimCore.TestSupport.Inputs.caseListPage (page))
            read)
        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Visibility precedes token parsing"

    Expect.equal
        (Queries.selectActors
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            {
                ActorListRequest.Role = None
                Text = None
                AfterCursor = page.AfterCursor
                Limit = page.Limit
            }
            read)
        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
        "People precedes token parsing"

    Expect.equal
        (Queries.selectActors
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            {
                ActorListRequest.Role = None
                Text = None
                AfterCursor = page.AfterCursor
                Limit = page.Limit
            }
            worker)
        (Error Refusal.InvalidCursor)
        "Authorized malformed cursor refuses"

    let target = actor other ((CaseFixtures.actorId (id 2))) [] []

    let grants: GrantsRequest =
        {
            ActorId = id 2
            AfterCursor = None
            Limit = 1
        }

    let caller = worker
    let view = target.Actor

    Expect.equal
        (Queries.selectGrants
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            grants
            caller
            view)
        (Ok None)
        "Enabled ungranted target is reviewable"

    let disabled =
        target.Actor |> Option.map (fun value -> { value with Enabled = false })

    Expect.equal
        (Queries.selectGrants
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            grants
            caller
            disabled)
        (Ok None)
        "Disabled target remains reviewable"

    Expect.equal
        (Queries.selectGrants
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            grants
            caller
            None)
        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Absent target remains private"

    Expect.equal
        (Queries.selectGrants
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            grants
            ungranted
            view)
        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Grant review needs installation people"

    Expect.equal
        (Advice.views owner ungranted)
        [ View.Operations ]
        "A valid ungranted actor retains recovery without business or people views"

    Expect.isEmpty (Advice.installationOffers owner ungranted) "Context carries no implied offers"

    Expect.isTrue
        (Access.validActor owner ungranted)
        "Context remains available to an enabled ungranted actor"

    Expect.isFalse (Access.validActor owner AccessView.empty) "Absent actor has no context"

let private history () =
    let _, entry, _, _ = created 460 "QUERY-HISTORY"

    let reader = actor owner ((CaseFixtures.actorId (id 1))) [ Role.CaseReader ] []

    Expect.isFalse
        (LedgerEntries.canObserve
            owner
            reader
            (EntryTarget.People((CaseFixtures.actorId (id 1)), PeopleActKind.Register)))
        "Case rights cannot observe people entries"

    Expect.isTrue
        (LedgerEntries.canObserve owner reader (LedgerEntries.primary entry).Subject)
        "Case rights observe accepted case metadata"

let private resources () =
    let _, _, current, _ = created 461 "QUERY-PRIVATE"
    let revoked = actor owner ((CaseFixtures.actorId (id 1))) [ Role.Owner ] []
    let read = (locked revoked (Some current) None).Read
    let expected = Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)

    Expect.equal
        (Queries.caseResult
            (DecisionFixtures.services installation calendar owner)
            read
            (Inputs.workView current.Snapshot.State)
            (Inputs.stageDisplay current.Snapshot.State)
            Inputs.workBasis.ObservedAt
            ClaimCore.TestSupport.ResponseExamples.caseRead.RecordedBy
            [])
        expected
        "Inaccessible live case"

    Expect.equal
        (Queries.caseResult
            (DecisionFixtures.services installation calendar owner)
            { read with Case = None }
            (Inputs.workView current.Snapshot.State)
            (Inputs.stageDisplay current.Snapshot.State)
            Inputs.workBasis.ObservedAt
            ClaimCore.TestSupport.ResponseExamples.caseRead.RecordedBy
            [])
        expected
        "Absent case is indistinguishable"

    let erased =
        { current with
            Snapshot =
                { current.Snapshot with
                    State = CaseState.Erased
                }
        }

    Expect.equal
        (Queries.caseResult
            (DecisionFixtures.services installation calendar owner)
            { read with Case = Some erased }
            (Inputs.workView erased.Snapshot.State)
            (Inputs.stageDisplay erased.Snapshot.State)
            Inputs.workBasis.ObservedAt
            ClaimCore.TestSupport.ResponseExamples.caseRead.RecordedBy
            [])
        expected
        "Erased beyond view is indistinguishable"

let private context () =
    let ungranted = actor owner ((CaseFixtures.actorId (id 1))) [] []

    let view = ungranted

    match
        Queries.contextResult
            (DecisionFixtures.services installation calendar owner)
            instant
            RegisterMaintenance.initial
            view
            []
    with
    | Outcome.Succeeded {
                            Requester = RequesterState.NoRoles value
                        } ->
        Expect.equal
            value.Self.ActorId
            (CaseFixtures.actorId (id 1))
            "Roleless context identifies only its caller"

        Expect.equal
            value.BusinessDate
            (calendar.DateAt instant)
            "Supplied instant owns business date"

        Expect.equal value.TimeZone calendar.TimeZoneId "Stored zone is retained"
    | other -> failtestf "Enabled ungranted actor needs NO_ROLES context, got %A" other

    match
        Queries.contextResult
            (DecisionFixtures.services installation calendar owner)
            instant
            RegisterMaintenance.initial
            AccessView.empty
            []
    with
    | Outcome.Succeeded {
                            Requester = RequesterState.NotRegistered value
                        } ->
        let _, issuer, subject = PrincipalKey.parts owner

        Expect.equal
            (value.Issuer, value.Subject)
            (issuer, subject)
            "Unregistered context reveals only caller identity"
    | other -> failtestf "Unregistered caller needs own requester state: %A" other

    let disabled =
        { ungranted with
            Actor = ungranted.Actor |> Option.map (fun value -> { value with Enabled = false })
        }

    match
        Queries.contextResult
            (DecisionFixtures.services installation calendar owner)
            instant
            RegisterMaintenance.initial
            disabled
            []
    with
    | Outcome.Succeeded {
                            Requester = RequesterState.Disabled self
                        } ->
        Expect.equal
            self.ActorId
            (CaseFixtures.actorId (id 1))
            "Disabled context identifies only its caller"
    | other -> failtestf "Disabled caller needs own requester state: %A" other

    let _, entry, current, _ = created 462 "SCOPED-AUDITOR"

    let scoped =
        actor owner ((CaseFixtures.actorId (id 1))) [] [ current.CaseId, [ Role.Auditor ] ]

    Expect.isTrue
        (LedgerEntries.canObserve owner scoped (LedgerEntries.primary entry).Subject)
        "Scoped auditor reads its case metadata"

    Expect.isFalse
        (LedgerEntries.canObserve
            owner
            scoped
            (EntryTarget.People((CaseFixtures.actorId (id 1)), PeopleActKind.Register)))
        "Scoped auditor cannot observe installation people"

    Expect.equal
        (Queries.selectActors
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            {
                Role = None
                Text = None
                AfterCursor = None
                Limit = 50
            }
            scoped)
        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Scoped audit cannot enumerate installation actors"

    Expect.isTrue
        (LedgerEntries.canObserve
            owner
            worker
            (EntryTarget.People((CaseFixtures.actorId (id 1)), PeopleActKind.Register)))
        "Installation owner may observe people"

[<Tests>]
let tests =
    testList
        "pure query selections"
        [
            testCase "context and people observation preserve installation boundaries" context
            testCase "absent inaccessible and erased resources share refusal" resources
            testCase "current people precedes cursors and target review" peopleAndCursor
            testCase "history detail and operation subject use current people" history
        ]
