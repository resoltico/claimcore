module ClaimCore.Tests.QuerySelectionTests

open Expecto
open ClaimCore.Domain
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

    let ungranted = actor owner (id 1) [] []

    let read = ungranted

    Expect.equal
        (Queries.selectCases owner page read)
        (Error Refusal.ResourceUnavailable)
        "Visibility precedes token parsing"

    Expect.equal
        (Queries.selectActors owner page read)
        (Error Refusal.ResourceUnavailable)
        "Authority precedes token parsing"

    Expect.equal
        (Queries.selectActors owner page worker)
        (Error Refusal.InvalidCursor)
        "Authorized malformed cursor refuses"

    let target = actor other (id 2) [] []

    let grants: GrantsRequest =
        {
            ActorId = id 2
            AfterCursor = None
            Limit = 1
        }

    let caller = worker
    let view = target.Actor

    Expect.equal
        (Queries.selectGrants owner grants caller view)
        (Ok None)
        "Enabled ungranted target is reviewable"

    let disabled =
        target.Actor |> Option.map (fun value -> { value with Enabled = false })

    Expect.equal
        (Queries.selectGrants owner grants caller disabled)
        (Ok None)
        "Disabled target remains reviewable"

    Expect.equal
        (Queries.selectGrants owner grants caller None)
        (Error Refusal.ResourceUnavailable)
        "Absent target remains private"

    Expect.equal
        (Queries.selectGrants owner grants ungranted view)
        (Error Refusal.ResourceUnavailable)
        "Grant review needs installation authority"

    Expect.isEmpty (Advice.views owner ungranted) "Ungrantable views are absent"
    Expect.isEmpty (Advice.installationOffers owner ungranted) "Context carries no implied offers"

    Expect.isTrue
        (Authority.validActor owner ungranted)
        "Context remains available to an enabled ungranted actor"

    Expect.isFalse (Authority.validActor owner AuthorityView.empty) "Absent actor has no context"

let private history () =
    let _, entry, current, _ = created 460 "QUERY-HISTORY"

    let reader = actor owner (id 1) [ Role.CaseReader ] []
    let read = Some current

    let request: HistoryRequest =
        {
            CaseReference = CaseReference.format current.Snapshot.Reference
            AfterCursor = None
            Limit = 1
            Detail = HistoryDetail.Summary
        }

    Expect.equal
        (Queries.selectHistory owner request reader read)
        (Ok 0L)
        "Reader may select summary"

    Expect.equal
        (Queries.selectHistory
            owner
            { request with
                Detail = HistoryDetail.Full
            }
            reader
            read)
        (Error Refusal.ResourceUnavailable)
        "Reader cannot select full history"

    Expect.equal
        (Queries.selectHistory owner request reader None)
        (Error Refusal.ResourceUnavailable)
        "Absent case stays private"

    Expect.isFalse
        (LedgerEntries.canObserve owner reader (EntryTarget.Authority(id 1, AuthorityKind.Register)))
        "Case rights cannot observe authority entries"

    Expect.isTrue
        (LedgerEntries.canObserve owner reader entry.Subject)
        "Case rights observe accepted case metadata"

let private resources () =
    let _, _, current, _ = created 461 "QUERY-PRIVATE"
    let revoked = actor owner (id 1) [ Role.Owner ] []
    let read = (locked revoked (Some current) None).Read
    let expected = ReadReply.Rejected Refusal.ResourceUnavailable
    Expect.equal (Queries.caseResult installation owner read) expected "Inaccessible live case"

    Expect.equal
        (Queries.caseResult installation owner { read with Case = None })
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
        (Queries.caseResult installation owner { read with Case = Some erased })
        expected
        "Erased beyond view is indistinguishable"

let private context () =
    let ungranted = actor owner (id 1) [] []

    let view = ungranted

    match Queries.contextResult BuildIdentity.current.Version calendar owner instant view with
    | ReadReply.Succeeded value ->
        Expect.isEmpty value.Capabilities "Enabled actor has context without implied capabilities"
        Expect.isEmpty value.Views "No views before grants"
        Expect.isEmpty value.Offers "No offers before grants"

        Expect.equal
            value.Runtime.EffectiveBusinessDate
            (calendar.DateAt instant)
            "Supplied instant owns business date"

        Expect.equal value.Runtime.TimeZoneId calendar.TimeZoneId "Stored zone is retained"
    | _ -> failtest "Enabled ungranted actor needs context"

    Expect.equal
        (Queries.contextResult
            BuildIdentity.current.Version
            calendar
            owner
            instant
            AuthorityView.empty)
        (ReadReply.Rejected Refusal.ResourceUnavailable)
        "Unregistered actor has no context"

    let disabled =
        { ungranted with
            Actor = ungranted.Actor |> Option.map (fun value -> { value with Enabled = false })
        }

    Expect.equal
        (Queries.contextResult BuildIdentity.current.Version calendar owner instant disabled)
        (ReadReply.Rejected Refusal.ResourceUnavailable)
        "Disabled actor has no context"

    let _, entry, current, _ = created 462 "SCOPED-AUDITOR"
    let scoped = actor owner (id 1) [] [ current.CaseId, [ Role.Auditor ] ]

    Expect.isTrue
        (LedgerEntries.canObserve owner scoped entry.Subject)
        "Scoped auditor reads its case metadata"

    Expect.isFalse
        (LedgerEntries.canObserve owner scoped (EntryTarget.Authority(id 1, AuthorityKind.Register)))
        "Scoped auditor cannot observe installation authority"

    Expect.equal
        (Queries.selectActors owner { AfterCursor = None; Limit = 50 } scoped)
        (Error Refusal.ResourceUnavailable)
        "Scoped audit cannot enumerate installation actors"

    Expect.isTrue
        (LedgerEntries.canObserve owner worker (EntryTarget.Authority(id 1, AuthorityKind.Register)))
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
