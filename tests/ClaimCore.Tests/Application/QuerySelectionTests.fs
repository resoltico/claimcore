module ClaimCore.Tests.QuerySelectionTests

open ClaimCore.TestSupport

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Tests.DecisionFixtures

let private authorityAndCursor () =
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
            (ClaimCore.TestSupport.Inputs.caseListPage (page))
            read)
        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Visibility precedes token parsing"

    Expect.equal
        (Queries.selectActors
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            page
            read)
        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Authority precedes token parsing"

    Expect.equal
        (Queries.selectActors
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            page
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
        "Grant review needs installation authority"

    Expect.equal
        (Advice.views owner ungranted)
        [ View.Operations ]
        "A valid ungranted actor retains recovery without business or authority views"

    Expect.isEmpty (Advice.installationOffers owner ungranted) "Context carries no implied offers"

    Expect.isTrue
        (Authority.validActor owner ungranted)
        "Context remains available to an enabled ungranted actor"

    Expect.isFalse (Authority.validActor owner AuthorityView.empty) "Absent actor has no context"

let private history () =
    let _, entry, current, _ = created 460 "QUERY-HISTORY"

    let reader = actor owner ((CaseFixtures.actorId (id 1))) [ Role.CaseReader ] []
    let read = Some current

    let request: HistoryRequest =
        {
            Target = CaseSelector.Reference(CaseReference.format current.Snapshot.Reference)
            AfterCursor = None
            Limit = 1
            Detail = HistoryDetail.Summary
        }

    Expect.equal
        (Queries.selectHistory
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            request
            reader
            read)
        (Ok(CaseFixtures.revision (0L)))
        "Reader may select summary"

    Expect.equal
        (Queries.selectHistory
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            { request with
                Detail = HistoryDetail.Full
            }
            reader
            read)
        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Reader cannot select full history"

    Expect.equal
        (Queries.selectHistory
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            request
            reader
            None)
        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Absent case stays private"

    Expect.isFalse
        (LedgerEntries.canObserve
            owner
            reader
            (EntryTarget.Authority((CaseFixtures.actorId (id 1)), AuthorityKind.Register)))
        "Case rights cannot observe authority entries"

    Expect.isTrue
        (LedgerEntries.canObserve owner reader entry.Subject)
        "Case rights observe accepted case metadata"

let private resources () =
    let _, _, current, _ = created 461 "QUERY-PRIVATE"
    let revoked = actor owner ((CaseFixtures.actorId (id 1))) [ Role.Owner ] []
    let read = (locked revoked (Some current) None).Read
    let expected = Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable)

    Expect.equal
        (Queries.caseResult (DecisionFixtures.services installation calendar owner) read)
        expected
        "Inaccessible live case"

    Expect.equal
        (Queries.caseResult
            (DecisionFixtures.services installation calendar owner)
            { read with Case = None })
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
            { read with Case = Some erased })
        expected
        "Erased beyond view is indistinguishable"

let private context () =
    let ungranted = actor owner ((CaseFixtures.actorId (id 1))) [] []

    let view = ungranted

    match
        Queries.contextResult (DecisionFixtures.services installation calendar owner) instant view
    with
    | Outcome.Succeeded value ->
        Expect.equal
            value.ActorId
            (CaseFixtures.actorId (id 1))
            "Context identifies its admitted requester"

        Expect.isEmpty value.Capability "Enabled actor has context without implied capabilities"

        Expect.equal
            value.View
            [ View.Operations ]
            "Roleless valid actors retain their own recovery area"

        Expect.isEmpty value.Offers "No offers before grants"

        Expect.equal
            value.Runtime.EffectiveBusinessDate
            (calendar.DateAt instant)
            "Supplied instant owns business date"

        Expect.equal value.Runtime.TimeZoneId calendar.TimeZoneId "Stored zone is retained"
    | _ -> failtest "Enabled ungranted actor needs context"

    Expect.equal
        (Queries.contextResult
            (DecisionFixtures.services installation calendar owner)
            instant
            AuthorityView.empty)
        (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Unregistered actor has no context"

    let disabled =
        { ungranted with
            Actor = ungranted.Actor |> Option.map (fun value -> { value with Enabled = false })
        }

    Expect.equal
        (Queries.contextResult
            (DecisionFixtures.services installation calendar owner)
            instant
            disabled)
        (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Disabled actor has no context"

    let _, entry, current, _ = created 462 "SCOPED-AUDITOR"

    let scoped =
        actor owner ((CaseFixtures.actorId (id 1))) [] [ current.CaseId, [ Role.Auditor ] ]

    Expect.isTrue
        (LedgerEntries.canObserve owner scoped entry.Subject)
        "Scoped auditor reads its case metadata"

    Expect.isFalse
        (LedgerEntries.canObserve
            owner
            scoped
            (EntryTarget.Authority((CaseFixtures.actorId (id 1)), AuthorityKind.Register)))
        "Scoped auditor cannot observe installation authority"

    Expect.equal
        (Queries.selectActors
            (System.Guid.Parse "90000000-0000-4000-8000-000000000001")
            owner
            { AfterCursor = None; Limit = 50 }
            scoped)
        (Error(Refusal.Access AccessRefusal.ResourceUnavailable))
        "Scoped audit cannot enumerate installation actors"

    Expect.isTrue
        (LedgerEntries.canObserve
            owner
            worker
            (EntryTarget.Authority((CaseFixtures.actorId (id 1)), AuthorityKind.Register)))
        "Installation owner may observe authority"

[<Tests>]
let tests =
    testList
        "pure query selections"
        [
            testCase "context and authority observation preserve installation boundaries" context
            testCase "absent inaccessible and erased resources share refusal" resources
            testCase "current authority precedes cursors and target review" authorityAndCursor
            testCase "history detail and operation subject use current authority" history
        ]
