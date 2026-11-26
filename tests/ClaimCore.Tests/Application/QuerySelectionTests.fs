module ClaimCore.Tests.QuerySelectionTests

open ClaimCore.TestSupport

open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Tests.ReadFixtures


let private peopleAndCursor () =
    let page: PageRequest =
        {
            AfterCursor = Some "malformed"
            Limit = 1
        }

    let ungranted = actor owner ((CaseFixtures.actorId (id 1))) [] []

    let read = ungranted

    Expect.equal
        (CaseSetQueries.selectCases
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            DomainInputs.workBasis
            (ClaimCore.TestSupport.Inputs.caseListPage (page))
            read)
        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Visibility precedes token parsing"

    Expect.equal
        (PeopleQueries.selectActors
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
        (PeopleQueries.selectActors
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
        (PeopleQueries.selectGrants
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
        (PeopleQueries.selectGrants
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            grants
            caller
            disabled)
        (Ok None)
        "Disabled target remains reviewable"

    Expect.equal
        (PeopleQueries.selectGrants
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            grants
            caller
            None)
        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Absent target remains private"

    Expect.equal
        (PeopleQueries.selectGrants
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
    let current = current ()

    let reader = actor owner ((CaseFixtures.actorId (id 1))) [ Role.CaseReader ] []

    Expect.isFalse
        (Engine.canObserve owner reader (AggregateReference.Person(CaseFixtures.actorId (id 1))))
        "Case rights cannot observe people entries"

    Expect.isTrue
        (Engine.canObserve owner reader (AggregateReference.Family current.CaseId))
        "Case rights observe accepted case metadata"

let private resources () =
    let current = current ()
    let revoked = actor owner ((CaseFixtures.actorId (id 1))) [ Role.Owner ] []

    let read: CaseReadFacts =
        {
            ObservedAt = instant
            Maintenance = RegisterMaintenance.initial
            Actor = revoked
            Case = Some current
            FamilySequence = Sequence.first
        }
    // An unavailable read must refuse before touching its transaction.
    let tx = Unchecked.defaultof<ReadTx>
    let expected = Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)

    Expect.equal
        (CaseDetailQueries.caseResult
            (ReadFixtures.services installation calendar owner)
            tx
            read
            (Inputs.workView current.Snapshot.State)
            (DomainInputs.stageDisplay current.Snapshot.State)
            DomainInputs.workBasis.ObservedAt
            ClaimCore.TestSupport.ResponseExamples.caseRead.RecordedBy
            []
         |> fun pending -> pending.GetAwaiter().GetResult())
        expected
        "Inaccessible live case"

    Expect.equal
        (CaseDetailQueries.caseResult
            (ReadFixtures.services installation calendar owner)
            tx
            { read with Case = None }
            (Inputs.workView current.Snapshot.State)
            (DomainInputs.stageDisplay current.Snapshot.State)
            DomainInputs.workBasis.ObservedAt
            ClaimCore.TestSupport.ResponseExamples.caseRead.RecordedBy
            []
         |> fun pending -> pending.GetAwaiter().GetResult())
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
        (CaseDetailQueries.caseResult
            (ReadFixtures.services installation calendar owner)
            tx
            { read with Case = Some erased }
            (Inputs.workView erased.Snapshot.State)
            (DomainInputs.stageDisplay erased.Snapshot.State)
            DomainInputs.workBasis.ObservedAt
            ClaimCore.TestSupport.ResponseExamples.caseRead.RecordedBy
            []
         |> fun pending -> pending.GetAwaiter().GetResult())
        expected
        "Erased beyond view is indistinguishable"

let private context () =
    let ungranted = actor owner ((CaseFixtures.actorId (id 1))) [] []

    let view = ungranted

    match
        RequesterContextQueries.contextResult
            (ReadFixtures.services installation calendar owner)
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
        RequesterContextQueries.contextResult
            (ReadFixtures.services installation calendar owner)
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
        RequesterContextQueries.contextResult
            (ReadFixtures.services installation calendar owner)
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

    let current = current ()

    let scoped =
        actor owner ((CaseFixtures.actorId (id 1))) [] [ current.CaseId, [ Role.Auditor ] ]

    Expect.isTrue
        (Engine.canObserve owner scoped (AggregateReference.Family current.CaseId))
        "Scoped auditor reads its case metadata"

    Expect.isFalse
        (Engine.canObserve owner scoped (AggregateReference.Person(CaseFixtures.actorId (id 1))))
        "Scoped auditor cannot observe installation people"

    Expect.equal
        (PeopleQueries.selectActors
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
        (Engine.canObserve owner worker (AggregateReference.Person(CaseFixtures.actorId (id 1))))
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
