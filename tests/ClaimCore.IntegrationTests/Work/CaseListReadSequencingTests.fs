module ClaimCore.IntegrationTests.CaseListReadSequencingTests

open System
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport

let private accepted result =
    Result.defaultWith (fun error -> failtestf "%A" error) result

let private visibleReference = "CC-0000-0000-0000-0000-98"
let private exactReference = "CC-0000-0000-0000-0001-95"
let private issuedKey = exactReference.ToLowerInvariant()

let private withCases check =
    let principal = PrincipalKey.human "https://issuer.example/" "reader" |> accepted

    let database =
        PostgresFixture.createFor principal
        |> fun pending -> pending.GetAwaiter().GetResult()

    use _owned =
        { new IDisposable with
            member _.Dispose() = database.Fixture.Dispose()
        }

    let operationId = StoreFixtures.id database.Fixture
    let admin = StoreFixtures.core database.Fixture principal

    let provisioned =
        Service.provisionOwner
            admin
            (operationId 2)
            {
                DisplayName = "Reader"
                DigestEmail = None
                Contact = None
            }
            []
            CancellationToken.None
        |> fun pending -> pending.GetAwaiter().GetResult()

    match provisioned with
    | Ok(Verdict.Proceed _) -> ()
    | other -> failtestf "Provisioning failed: %A" other

    for number, role in
        [ 10000, Role.CaseReader; 10001, Role.CaseRegistrar; 10002, Role.DataSteward ] do
        let operation = operationId number

        let granted =
            StoreFixtures.grant
                admin
                operation
                principal
                {
                    Role = role
                    Scope = GrantTarget.Installation
                }
                true
            |> fun pending -> pending.GetAwaiter().GetResult()

        StoreFixtures.acceptedPeople granted

    let core principal =
        Service.create
            "synthetic"
            database.Fixture.InstallationId
            database.Fixture.Store
            database.Fixture.Calendar
            principal

    let current = core principal

    for index, policy in List.indexed [ exactReference + " matching-policy"; "matching-policy" ] do
        let draw () =
            let bytes = Array.zeroCreate<byte> CaseReference.EntropyBytes
            bytes[bytes.Length - 1] <- byte index
            bytes

        let opener =
            current
            |> Service.mapStore (fun _ ->
                LedgerStore(
                    database.RuntimeSource,
                    database.Installation,
                    database.Cipher,
                    Transactions.timeouts,
                    caseReferenceEntropy = draw
                )
                :> IStore)

        let reply =
            Service.executeSubmission
                opener
                (Submissions.createRaw
                    "case.open"
                    (operationId (index + 3))
                    (OperationBinding.CaseNew 0L)
                    (Map.ofList
                        [
                            Submissions.field BusinessField.PolicyNumber,
                            SubmissionValue.Text policy
                        ]))
                CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()

        StoreFixtures.accepted reply |> ignore

    let scopedPrincipal =
        PrincipalKey.human "https://issuer.example/" "scoped" |> accepted

    let registered =
        (task {
            let! submission =
                ClaimCore.TestSupport.StoreFixtures.personSubmission
                    (admin).Store
                    ((operationId 10))
                    (scopedPrincipal)
                    (Some 0L)
                    "people.register"
                    (ClaimCore.TestSupport.Submissions.profile (DomainInputs.actorProfile))

            return! Service.executeSubmission (admin) submission (CancellationToken.None)
        })
        |> fun pending -> pending.GetAwaiter().GetResult()

    StoreFixtures.acceptedPeople registered

    let granted =
        StoreFixtures.grant
            admin
            (operationId 10003)
            scopedPrincipal
            {
                Role = Role.CaseReader
                Scope = GrantTarget.CaseId(CaseFixtures.caseId (operationId 3))
            }
            true
        |> fun pending -> pending.GetAwaiter().GetResult()

    StoreFixtures.acceptedPeople granted
    let scoped = core scopedPrincipal
    check current scoped operationId

let private read core text statuses limit =
    Service.list
        core
        {
            Page = { AfterCursor = None; Limit = limit }
            Filter = CaseListFilter.create [] statuses [] (Some text) CaseListOrder.Reference
        }
        CancellationToken.None
    |> fun task -> task.GetAwaiter().GetResult()

let private page =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected successful list: %A" other

let private expectExact operationId (value: CaseSummaryPage) =
    Expect.equal
        value.ExactReference
        (Some
            {
                CaseId = CaseFixtures.caseId (operationId 4)
                CaseReference = ClaimCore.TestSupport.DomainInputs.reference exactReference
            })
        "Issued reference and creation identity"

[<Tests>]
let tests =
    testList
        "case-list exact reference disclosure"
        [
            testCase
                "empty scope ignores lifecycle filters and only applies to initial pages"
                (fun () ->
                    withCases (fun core _ _ ->
                        let all =
                            { CaseListFilter.empty with
                                View = Some CaseView.All
                            }

                        let read filter after limit =
                            Service.list
                                core
                                {
                                    Page = { AfterCursor = after; Limit = limit }
                                    Filter = filter
                                }
                                CancellationToken.None
                            |> fun pending -> pending.GetAwaiter().GetResult()
                            |> page

                        for lifecycle in [ Lifecycle.Erased; Lifecycle.Voided ] do
                            let filtered = read { all with Lifecycles = [ lifecycle ] } None 50

                            Expect.isEmpty filtered.Items "The fixture only has active cases"

                            Expect.isFalse
                                filtered.AuthorizedScopeEmpty
                                "The active cases remain in scope"

                        let first = read all None 1
                        Expect.isSome first.NextCursor "The initial page has a continuation"

                        let trailing = read all first.NextCursor 1

                        Expect.equal
                            (trailing.Items
                             |> List.map (fun item -> CaseReference.format item.Reference))
                            [ exactReference ]
                            "The continuation returns the remaining readable case"

                        Expect.isNone
                            trailing.NextCursor
                            "The continuation completes the traversal"

                        Expect.isFalse
                            trailing.AuthorizedScopeEmpty
                            "Only initial pages assess scope"))
            testCase
                "visible exact identity and original spelling survive case-insensitive matching"
                (fun () ->
                    withCases (fun core _ operationId ->
                        let result = read core issuedKey [] 50 |> page
                        expectExact operationId result

                        Expect.equal
                            (result.Items
                             |> List.map (fun item -> CaseReference.format item.Reference))
                            [ visibleReference; exactReference ]
                            "Independently selected rows"))
            testCase
                "authorized exact identity remains available outside the current page or filter"
                (fun () ->
                    withCases (fun core _ operationId ->
                        read core issuedKey [] 1 |> page |> expectExact operationId
                        let empty = read core issuedKey [ CaseStatus.Closed ] 50 |> page
                        Expect.isEmpty empty.Items "No closed rows"

                        Expect.isFalse
                            empty.AuthorizedScopeEmpty
                            "The authorized exact result remains available"

                        expectExact operationId empty))
            testCase
                "partial, absent and malformed references do not become exact identities"
                (fun () ->
                    withCases (fun core _ _ ->
                        for query, count in [ "matching-policy", 2; "MISSING", 0; "two words", 0 ] do
                            let result = read core query [] 50 |> page
                            Expect.equal result.Items.Length count "Independently selected rows"

                            Expect.isFalse
                                result.AuthorizedScopeEmpty
                                "Display filters do not define authorized scope"

                            Expect.isNone result.ExactReference "No exact identity"))
            testCase
                "unreadable exact target stays undisclosed beside a visible partial match"
                (fun () ->
                    withCases (fun _ scoped _ ->
                        let result = read scoped issuedKey [] 50 |> page

                        Expect.equal
                            (result.Items
                             |> List.map (fun item -> CaseReference.format item.Reference))
                            [ visibleReference ]
                            "Only the granted case is disclosed"

                        Expect.isNone
                            result.ExactReference
                            "The unreadable exact identity is withheld"

                        let visible =
                            read scoped (visibleReference.ToLowerInvariant()) [] 50 |> page

                        let hidden = read scoped issuedKey [ CaseStatus.Closed ] 50 |> page
                        let unknown = read scoped "unknown-2" [ CaseStatus.Closed ] 50 |> page

                        Expect.isEmpty hidden.Items "The display filter excludes the visible peer"

                        Expect.isNone
                            hidden.ExactReference
                            "The hidden reference still stays hidden"

                        Expect.isFalse
                            hidden.AuthorizedScopeEmpty
                            "The principal has one readable case"

                        Expect.equal
                            hidden.AuthorizedScopeEmpty
                            unknown.AuthorizedScopeEmpty
                            "Hidden and unknown references give the same empty outcome"

                        Expect.equal
                            (visible.ExactReference
                             |> Option.map (fun item -> CaseReference.format item.CaseReference))
                            (Some visibleReference)
                            "Scoped visible exact identity"))
            testCase "erasure preserves exact authorized reference identity" (fun () ->
                withCases (fun core _ operationId ->
                    for operation, revision, taskName, values in
                        [
                            31,
                            1L,
                            "case.void",
                            Map.ofList
                                [
                                    Submissions.input InputTarget.Reason,
                                    SubmissionValue.Text "Retained reason"
                                ]
                            32, 2L, "case.erase", Map.empty
                        ] do
                        let family =
                            if taskName = "case.erase" then
                                Some(
                                    StoreFixtures.reviewedFamilySequence
                                        core.Store
                                        (CaseSelector.Reference exactReference)
                                )
                            else
                                None

                        let reply =
                            Service.executeSubmission
                                core
                                (Submissions.createRaw
                                    taskName
                                    (operationId operation)
                                    (OperationBinding.Case(
                                        CaseSelector.Reference exactReference,
                                        revision,
                                        None,
                                        family
                                    ))
                                    values)
                                CancellationToken.None
                            |> fun task -> task.GetAwaiter().GetResult()

                        match reply.Outcome with
                        | ExecuteResult.Accepted _ -> ()
                        | other -> failtestf "Fixture erasure failed: %A" other

                    read core issuedKey [] 50 |> page |> expectExact operationId))
        ]
