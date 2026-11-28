module ClaimCore.IntegrationTests.CaseReferenceTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Application.Verification
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None

let private first = "CC-0000-0000-0000-0000-98"
let private second = "CC-0000-0000-0000-0001-95"

let private seed number =
    let bytes = Array.zeroCreate<byte> 10
    bytes[9] <- number
    bytes

let private succeeds =
    function
    | Outcome.Succeeded result -> result
    | other -> failtestf "Expected success, received %A" other

let private run action =
    async {
        let! database = PostgresFixture.createWithOwner () |> Async.AwaitTask

        try
            do! action database |> Async.AwaitTask
        finally
            database.Fixture.Dispose()
    }

let private withEntropy (database: PostgresFixture.Database) draw =
    let service = core database.Fixture database.Fixture.Owner

    { service with
        Store =
            LedgerStore(
                database.RuntimeSource,
                database.Installation,
                database.Cipher,
                Transactions.timeouts,
                caseReferenceEntropy = draw
            )
            :> IStore
    }

let private opening fixture number values =
    Submissions.create "case.open" (id fixture number) (Binding.CaseNew Revision.zero) values

let private readBy service selector =
    task {
        let! result = Service.get service { Target = selector } token
        return succeeds result
    }

let private assigned service submission =
    task {
        let! result = Service.executeSubmission service submission token
        return accepted result |> caseReceipt
    }

let private head (database: PostgresFixture.Database) =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        return! OperationStorage.position connection token
    }

let private verify (database: PostgresFixture.Database) =
    task {
        let! result = Verification.verify database.OwnerSource database.MasterKey None token

        match result with
        | VerificationResult.Verified _ -> ()
        | other ->
            failtestf "Expected complete retained and projection verification, received %A" other
    }

let private issuedIdentity (database: PostgresFixture.Database) =
    task {
        let fixture = database.Fixture
        let mutable draws = 0uy

        let service =
            withEntropy database (fun () ->
                let entropy = seed draws
                draws <- draws + 1uy
                entropy)

        let draft =
            opening
                fixture
                20
                (Map.ofList
                    [
                        Submissions.field BusinessField.PolicyNumber,
                        SubmissionValue.Text "SYNTHETIC-POLICY-42"
                    ])

        let! review = Service.previewSubmission service draft token

        match succeeds review.Outcome with
        | Previewed.Reviewed { Target = ReviewTarget.Opening } -> ()
        | other -> failtestf "Expected opening review, got %A" other

        Expect.equal draws 0uy "Review does not allocate the reference"

        let! receipt = assigned service draft
        Expect.equal (CaseReference.format receipt.Reference) first "Reference chosen by server"
        Expect.equal (CaseId.value receipt.CaseId) (id fixture 20) "Stable Case ID"

        let byId = CaseSelector.Id receipt.CaseId
        let! exact = readBy service (CaseSelector.Reference(first.ToLowerInvariant()))
        let! stable = readBy service byId

        Expect.equal
            exact.Snapshot
            stable.Snapshot
            "Generated reference and durable ID resolve the same case"

        let list () =
            Service.list
                service
                {
                    CaseListRequest.Filter =
                        { CaseListFilter.empty with
                            Text = Some "SYNTHETIC-POLICY-42"
                        }
                    Page = { Limit = 5; AfterCursor = None }
                }
                token

        let! matches = list ()

        Expect.equal
            ((succeeds matches).Items |> List.map _.CaseId)
            [ receipt.CaseId ]
            "External identifiers are searchable business facts"

        let voided =
            Submissions.createRaw
                "case.void"
                (id fixture 21)
                (OperationBinding.Case(byId, 1L, None, None))
                (Map.ofList
                    [
                        Submissions.input InputTarget.Reason,
                        SubmissionValue.Text "synthetic cleanup"
                    ])

        let! result = Service.executeSubmission service voided token
        accepted result |> ignore

        let erase =
            Submissions.createRaw
                "case.erase"
                (id fixture 22)
                (OperationBinding.Case(
                    byId,
                    2L,
                    None,
                    Some(reviewedErasureToken fixture.Store fixture.Owner byId)
                ))
                Map.empty

        let! result = Service.executeSubmission service erase token
        accepted result |> ignore
        let! erased = readBy service (CaseSelector.Reference first)

        Expect.equal
            erased.Snapshot.State
            CaseState.Erased
            "The issued reference resolves erased evidence"

        let! disappeared = list ()

        Expect.isEmpty
            (succeeds disappeared).Items
            "Erasure removes the external identifier search match"

        do! verify database
    }

let private collisions (database: PostgresFixture.Database) =
    task {
        let mutable draws = 0
        let mutable entropy = seed 0uy

        let service =
            withEntropy database (fun () ->
                draws <- draws + 1
                entropy)

        let original = opening database.Fixture 30 Map.empty

        let! results =
            Task.WhenAll(
                Service.executeSubmission service original token,
                Service.executeSubmission service original token
            )

        for result in results do
            match result.Outcome with
            | ExecuteResult.Accepted receipt
            | ExecuteResult.AlreadyAccepted receipt ->
                Expect.equal
                    (CaseReference.format (caseReceipt receipt).Reference)
                    first
                    "Retry recovers the original reference"
            | other -> failtestf "Unexpected opening outcome: %A" other

        Expect.equal draws 1 "One opening allocates once"
        let! checkpoint = head database
        let next = opening database.Fixture 31 Map.empty
        let! exhausted = Service.executeSubmission service next token

        Expect.equal
            exhausted.Outcome
            (ExecuteResult.Failed CoreFault.InternalError)
            "Occupied candidates fail after the draw limit"

        Expect.equal draws 9 "One prior draw and eight collision attempts"
        let! unchanged = head database
        Expect.equal unchanged checkpoint "Collision exhaustion writes no ledger effect"
        entropy <- seed 1uy
        let! fresh = assigned service next

        Expect.equal
            (CaseReference.format fresh.Reference)
            second
            "A free generated reference remains assignable"

        do! verify database
    }

[<Tests>]
let tests =
    testList
        "permanent issued references"
        [
            testCaseAsync
                "generated lookup survives erasure while an external identifier clears"
                (run issuedIdentity)
            testCaseAsync
                "opening retries and collisions preserve a single immutable reference"
                (run collisions)
        ]
