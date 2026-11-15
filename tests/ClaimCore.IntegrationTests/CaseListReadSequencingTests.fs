module ClaimCore.IntegrationTests.CaseListReadSequencingTests

open System
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.TestSupport

let private accepted result =
    Result.defaultWith (fun error -> failtestf "%A" error) result

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

    for index, reference in List.indexed [ "A-MATCH-1"; "Match-1" ] do
        let reply =
            Service.execute
                current
                (Inputs.caseOperation
                    {
                        ExpectedFamilySequence = None
                        OperationId = operationId (index + 3)
                        CaseReference = reference
                        ExpectedRevision = 0L
                        Act = AuthoredCaseAct.Open Inputs.registration
                    })
                CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()

        match reply.Outcome with
        | ExecuteResult.Accepted _ -> ()
        | other -> failtestf "Fixture opening failed: %A" other

    let scopedPrincipal =
        PrincipalKey.human "https://issuer.example/" "scoped" |> accepted

    let registered =
        Service.execute
            admin
            (OperationInput.People(
                StoreFixtures.peopleRequest
                    (operationId 10)
                    scopedPrincipal
                    (PeopleAct.Register Inputs.actorProfile)
            ))
            CancellationToken.None
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
                CaseReference = CaseReference.parse "Match-1" |> accepted
            })
        "Original authored reference and creation identity"

[<Tests>]
let tests =
    testList
        "case-list exact reference disclosure"
        [
            testCase
                "visible exact identity and original spelling survive case-insensitive matching"
                (fun () ->
                    withCases (fun core _ operationId ->
                        let result = read core "match-1" [] 50 |> page
                        expectExact operationId result

                        Expect.equal
                            (result.Items
                             |> List.map (fun item -> CaseReference.format item.Reference))
                            [ "A-MATCH-1"; "Match-1" ]
                            "Independently selected rows"))
            testCase
                "authorized exact identity remains available outside the current page or filter"
                (fun () ->
                    withCases (fun core _ operationId ->
                        read core "match-1" [] 1 |> page |> expectExact operationId
                        let empty = read core "match-1" [ CaseStatus.Closed ] 50 |> page
                        Expect.isEmpty empty.Items "No closed rows"
                        expectExact operationId empty))
            testCase
                "partial, absent and malformed references do not become exact identities"
                (fun () ->
                    withCases (fun core _ _ ->
                        for query, count in [ "match", 2; "MISSING", 0; "two words", 0 ] do
                            let result = read core query [] 50 |> page
                            Expect.equal result.Items.Length count "Independently selected rows"
                            Expect.isNone result.ExactReference "No exact identity"))
            testCase
                "unreadable exact target stays undisclosed beside a visible partial match"
                (fun () ->
                    withCases (fun _ scoped _ ->
                        let result = read scoped "match-1" [] 50 |> page

                        Expect.equal
                            (result.Items
                             |> List.map (fun item -> CaseReference.format item.Reference))
                            [ "A-MATCH-1" ]
                            "Only the granted case is disclosed"

                        Expect.isNone
                            result.ExactReference
                            "The unreadable exact identity is withheld"

                        let visible = read scoped "a-match-1" [] 50 |> page

                        Expect.equal
                            (visible.ExactReference
                             |> Option.map (fun item -> CaseReference.format item.CaseReference))
                            (Some "A-MATCH-1")
                            "Scoped visible exact identity"))
            testCase "erasure preserves exact authorized reference identity" (fun () ->
                withCases (fun core _ operationId ->
                    for operation, revision, act in
                        [
                            31, 1L, AuthoredCaseAct.Void "Retained reason"
                            32, 2L, AuthoredCaseAct.Bare BareAct.Erase
                        ] do
                        let reply =
                            Service.execute
                                core
                                (Inputs.caseOperation
                                    {
                                        ExpectedFamilySequence =
                                            if CaseAct.kind act = CaseActKind.Erase then
                                                Some(
                                                    ClaimCore.TestSupport.StoreFixtures
                                                        .reviewedFamilySequence
                                                        core.Store
                                                        (CaseSelector.Reference "Match-1")
                                                )
                                            else
                                                None
                                        OperationId = operationId operation
                                        CaseReference = "Match-1"
                                        ExpectedRevision = revision
                                        Act = act
                                    })
                                CancellationToken.None
                            |> fun task -> task.GetAwaiter().GetResult()

                        match reply.Outcome with
                        | ExecuteResult.Accepted _ -> ()
                        | other -> failtestf "Fixture erasure failed: %A" other

                    read core "match-1" [] 50 |> page |> expectExact operationId))
        ]
