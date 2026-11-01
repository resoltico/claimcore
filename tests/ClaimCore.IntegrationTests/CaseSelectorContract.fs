module internal ClaimCore.TestSupport.CaseSelectorContract

open System
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.Json
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let identities (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let reference = "80000000-0000-4000-8000-000000000609"
        let expected = CaseFixtures.caseId (id 610)

        let openInput: CaseOperationRequest =
            {
                OperationId = id 610
                Target = CaseSelector.Reference reference
                ExpectedRevision = 0L
                Act = AuthoredCaseAct.Open registration
            }

        let! opened = Service.execute actor (OperationInput.Case openInput) token
        let opening = accepted opened

        Expect.equal
            (caseReceipt opening).CaseId
            expected
            "OPEN identity is its operation UUID, independently of its UUID-shaped reference"

        let! byReference =
            Service.get
                actor
                {
                    Target = CaseSelector.Reference reference
                }
                token

        let! byId = Service.get actor { Target = CaseSelector.Id expected } token

        match byId, byReference with
        | Outcome.Succeeded actual, Outcome.Succeeded expected ->
            for view in [ actual; expected ] do
                Expect.equal view.Work.Basis.TimeZoneId "Etc/UTC" "The fixture uses UTC"

                Expect.equal
                    view.Work.Basis.BusinessDate
                    (DateOnly.FromDateTime view.Work.Basis.ObservedAt.UtcDateTime)
                    "Each read reports its own observation basis"

            Expect.equal
                { actual with
                    Work =
                        { actual.Work with
                            Basis = expected.Work.Basis
                        }
                }
                expected
                "Both selectors return the same case facts and current authority across distinct observations"
        | _ -> failtest "Both positive selectors must resolve"

        match byId with
        | Outcome.Succeeded view ->
            Expect.equal view.Snapshot.CaseId expected "Public read identity"
        | _ -> failtest "The independent positive ID selector must resolve"

        let! ambiguous =
            Service.get
                actor
                {
                    Target = CaseSelector.Id(CaseFixtures.caseId (id 609))
                }
                token

        Expect.equal
            ambiguous
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "An ID selector never reinterprets its UUID as a reference"

        let close: CaseOperationRequest =
            {
                OperationId = id 611
                Target = CaseSelector.Id expected
                ExpectedRevision = 1L
                Act = AuthoredCaseAct.Bare BareAct.Close
            }

        let! closed = Service.execute actor (OperationInput.Case close) token
        let receipt = accepted closed

        Expect.equal
            (caseReceipt receipt).CaseId
            expected
            "Mutation receipt retains the creation ID"

        let! replay =
            Service.execute
                actor
                (OperationInput.Case
                    { close with
                        Target = CaseSelector.Reference reference
                    })
                token

        Expect.equal
            replay.Outcome
            (ExecuteResult.AlreadyAccepted receipt)
            "Resolved identity makes reference and ID retries equivalent"

        let query: HistoryRequest =
            {
                Target = CaseSelector.Id expected
                AfterCursor = None
                Limit = 1
                Detail = HistoryDetail.Full
            }

        let! history = Service.history actor query token

        let first =
            match history with
            | Outcome.Succeeded page -> page
            | _ -> failtest "Authorized ID history must resolve"

        Expect.equal
            (first.CaseId, CaseReference.format first.Reference)
            (expected, reference)
            "History publishes the retained identity"

        match first.Entries with
        | [ HistoryEntry.FullEntry full ] ->
            Expect.equal
                full.Difference
                (Some
                    {
                        Lifecycle = None
                        Fields =
                            [
                                {
                                    FieldName = BusinessField.Status
                                    Before = Some "OPENED"
                                    After = Some "CLOSED"
                                }
                            ]
                    })
                "The already-fetched predecessor supplies an independent bounded comparison"
        | _ -> failtest "Expected one full receipt"

        Expect.hasLength first.Entries 1 "Bounded live page"
        Expect.isSome first.NextCursor "Independent older acceptance remains"

        let! continuation =
            Service.history
                actor
                { query with
                    Target = CaseSelector.Reference reference
                    AfterCursor = first.NextCursor
                }
                token

        match continuation with
        | Outcome.Succeeded page ->
            Expect.hasLength
                page.Entries
                1
                "A continuation follows the same resource through either selector"

            Expect.isNone page.NextCursor "The opening is the final live keyset row"
        | _ -> failtest "Equivalent selector continuation must remain valid"

        let! second =
            Service.execute
                actor
                (OperationInput.Case
                    { openInput with
                        OperationId = id 612
                        Target = CaseSelector.Reference "SELECTOR-OTHER"
                    })
                token

        Expect.equal
            (caseReceipt (accepted second)).CaseId
            (CaseFixtures.caseId (id 612))
            "Another OPEN has a distinct creation identity"

        let! wrongTarget =
            Service.execute
                actor
                (OperationInput.Case
                    { close with
                        Target = CaseSelector.Id(CaseFixtures.caseId (id 612))
                    })
                token

        Expect.equal
            wrongTarget.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "An accepted operation cannot be redirected to another case"

        let! moved =
            Service.history
                actor
                { query with
                    Target = CaseSelector.Id(CaseFixtures.caseId (id 612))
                    AfterCursor = first.NextCursor
                }
                token

        Expect.equal
            moved
            (Outcome.Rejected Refusal.InvalidCursor)
            "History continuations cannot be moved to another retained ID"

        let! stewardship =
            grant
                actor
                (id 620)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority stewardship

        let! voided =
            Service.execute
                actor
                (OperationInput.Case
                    { close with
                        OperationId = id 621
                        ExpectedRevision = 2L
                        Act = AuthoredCaseAct.Void "Synthetic retained identity check"
                    })
                token

        accepted voided |> ignore

        let! erased =
            Service.execute
                actor
                (OperationInput.Case
                    { close with
                        OperationId = id 622
                        ExpectedRevision = 3L
                        Act = AuthoredCaseAct.Bare BareAct.Erase
                    })
                token

        accepted erased |> ignore
        let! tombstone = Service.get actor { Target = CaseSelector.Id expected } token

        match tombstone with
        | Outcome.Succeeded view ->
            Expect.equal
                (view.Snapshot.CaseId, CaseReference.format view.Snapshot.Reference)
                (expected, reference)
                "Erasure retains immutable identity"

            Expect.equal view.Snapshot.State CaseState.Erased "Business content is erased"
        | _ -> failtest "Retained tombstone must remain addressable"

        let! reuse =
            Service.execute
                actor
                (OperationInput.Case { openInput with OperationId = id 623 })
                token

        match reuse.Outcome with
        | ExecuteResult.Rejected _ -> ()
        | _ -> failtest "An erased case reference cannot be reused"

        let! fresh =
            Service.execute
                actor
                (OperationInput.Case
                    { openInput with
                        OperationId = id 624
                        Target = CaseSelector.Reference "SELECTOR-FRESH"
                    })
                token

        accepted fresh |> ignore
    }

let closedSelectorGrammar () =
    let input =
        JsonValue.Object
            [
                "kind", JsonValue.String "ID"
                "caseId", JsonValue.String "80000000-0000-4000-8000-000000000610"
            ]

    Expect.equal
        (RequestCodecs.caseSelector.Read "$" input)
        (Ok(CaseSelector.Id(CaseFixtures.caseId (id 610))))
        "Independent ID grammar positive control"

    let mixed =
        match input with
        | JsonValue.Object fields ->
            JsonValue.Object(fields @ [ "caseReference", JsonValue.String "OTHER" ])
        | _ -> failtest "Object control"

    Expect.isError
        (RequestCodecs.caseSelector.Read "$" mixed)
        "An ID/reference pair cannot redirect a request"

let grantScopes (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let expectedId = CaseFixtures.caseId (id 630)
        let reference = CaseFixtures.reference "Scope-A"

        let! opened =
            Service.execute
                actor
                (Inputs.caseOperation (request 630 "Scope-A" 0L (AuthoredCaseAct.Open registration)))
                token

        Expect.equal (caseReceipt (accepted opened)).CaseId expectedId "Independent creation ID"

        let reader =
            PrincipalKey.human "https://issuer.example/" "scope-reader"
            |> Result.defaultWith (string >> failtest)

        let! registered =
            Service.execute
                actor
                (OperationInput.Authority(authorityRequest (id 631) reader AuthorityAct.Register))
                token

        acceptedAuthority registered

        let authored =
            {
                AuthoredGrant.Role = Role.CaseReader
                Scope = GrantTarget.CaseId expectedId
            }

        let expected: ScopedGrant =
            {
                Role = Role.CaseReader
                Scope = GrantScope.Case(expectedId, reference)
            }

        let input = authorityRequest (id 632) reader (AuthorityAct.Grant authored)
        let! preview = Service.preview actor (OperationInput.Authority input) token

        match preview.Outcome with
        | Outcome.Succeeded(Previewed.Reviewed(OperationReview.Authority review)) ->
            Expect.equal
                review.After.Grants
                [ expected ]
                "Review publishes the resolved immutable scope"
        | _ -> failtest "The independent ID-scoped grant must preview"

        let! granted = Service.execute actor (OperationInput.Authority input) token
        let acceptedGrant = accepted granted

        let! replay =
            Service.execute
                actor
                (OperationInput.Authority
                    { input with
                        Act =
                            AuthorityAct.Grant
                                { authored with
                                    Scope = GrantTarget.CaseReference "scope-a"
                                }
                    })
                token

        Expect.equal
            replay.Outcome
            (ExecuteResult.AlreadyAccepted acceptedGrant)
            "Reference and ID grants share canonical accepted identity"

        let! stored =
            fixture.Store.Read
                {
                    Work = (fun tx -> tx.FindEntry(CaseFixtures.operationId (id 632)))
                    CancellationToken = token
                }

        let entry =
            match stored with
            | Ok(Some value) -> value
            | _ -> failtest "The real accepted authority record must exist"

        let payload =
            LedgerEntries.payload entry
            |> Result.defaultWith (fun _ -> failtest "Salted payload positive control")

        let permanent =
            ClaimCore.RecordFormat.AuthorityRecord.decode 65536 payload.Request
            |> Result.defaultWith (fun _ -> failtest "Permanent scope positive control")

        Expect.equal
            permanent.Act
            (AuthorityAct.Grant
                {
                    Role = Role.CaseReader
                    Scope = ClaimCore.RecordFormat.RecordedGrantScope.Case(expectedId, reference)
                })
            "Permanent request retains the actual ID/reference pair"

        let query: GrantsRequest =
            {
                ActorId = id 631
                AfterCursor = None
                Limit = 50
            }

        let! page = Service.grants actor query token

        match page with
        | Outcome.Succeeded page ->
            Expect.equal page.Items [ expected ] "Grant reads publish the same retained identity"
        | _ -> failtest "Scoped grant page must succeed"

        let scopedReader = core fixture reader
        let! visible = Service.get scopedReader { Target = CaseSelector.Id expectedId } token

        Expect.isTrue
            (match visible with
             | Outcome.Succeeded _ -> true
             | _ -> false)
            "ID lookup enforces the granted case scope"

        let! revoked =
            grant
                actor
                (id 633)
                reader
                { authored with
                    Scope = GrantTarget.CaseReference "scope-a"
                }
                false

        acceptedAuthority revoked
        let! denied = Service.get scopedReader { Target = CaseSelector.Id expectedId } token

        Expect.equal
            denied
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "The next lookup reauthorizes after revocation"

        let! restored = grant actor (id 634) reader authored true
        acceptedAuthority restored

        let! stewardship =
            grant
                actor
                (id 635)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority stewardship

        let! voided =
            Service.execute
                actor
                (OperationInput.Case
                    {
                        OperationId = id 636
                        Target = CaseSelector.Id expectedId
                        ExpectedRevision = 1L
                        Act = AuthoredCaseAct.Void "Synthetic scope retention"
                    })
                token

        accepted voided |> ignore

        let! erased =
            Service.execute
                actor
                (OperationInput.Case
                    {
                        OperationId = id 637
                        Target = CaseSelector.Id expectedId
                        ExpectedRevision = 2L
                        Act = AuthoredCaseAct.Bare BareAct.Erase
                    })
                token

        accepted erased |> ignore
        let! retained = Service.grants actor query token

        match retained with
        | Outcome.Succeeded page ->
            Expect.equal
                page.Items
                [ expected ]
                "Erasure cannot change the immutable grant identity"
        | _ -> failtest "Erased grant metadata remains readable"

        let! history =
            Service.authorityHistory
                actor
                {
                    TargetActorId = Some(id 631)
                    AfterCursor = None
                    Limit = 50
                }
                token

        match history with
        | Outcome.Succeeded page ->
            let scopes =
                page.Entries
                |> List.choose (fun entry ->
                    match entry.Facts with
                    | AuthorityHistoryFacts.Granted value
                    | AuthorityHistoryFacts.Revoked value -> Some value
                    | _ -> None)

            Expect.equal
                scopes
                [ expected; expected; expected ]
                "History reads retained pairs without recovering erased case data"
        | _ -> failtest "Authority history remains readable after case erasure"
    }
