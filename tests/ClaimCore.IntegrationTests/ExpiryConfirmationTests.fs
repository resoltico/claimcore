module ClaimCore.IntegrationTests.ExpiryConfirmationTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

let private token = CancellationToken.None
let private sync (pending: Task<'a>) = pending.GetAwaiter().GetResult()

let private owner fixture =
    let service = core fixture fixture.Owner

    match
        PeopleOperations.provisionStore
            service
            (id fixture 1)
            Inputs.ownerProfile
            People.initialOwnerRoles
            token
        |> sync
    with
    | Ok(Verdict.Proceed _) -> service
    | other -> failtestf "Owner bootstrap: %A" other

let private frontier (database: PostgresFixture.Database) =
    DatabaseSession.read database.RuntimeSource token (fun connection ->
        ExpiryFrontierStorage.read connection token)
    |> sync

let private oldIdentity () =
    Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes -11)

let private lookup service identity cancellation =
    let reply =
        Service.operationGet service { OperationId = identity } cancellation |> sync

    Expect.equal reply.OperationId identity "Lookup keeps exact operation correlation"
    reply.Outcome

let private openCase identity reference =
    Inputs.caseOperation
        {
            OperationId = identity
            CaseReference = reference
            ExpectedRevision = 0L
            Act = AuthoredCaseAct.Open Inputs.registration
        }

let private confirmed () =
    let database = PostgresFixture.create () |> sync

    try
        let fixture = database.Fixture
        let service = owner fixture
        let original = openCase (Guid.CreateVersion7()) "EXPIRY-AVAILABLE"
        let acceptedReceipt = Service.execute service original token |> sync |> accepted
        let erasedInput = openCase (Guid.CreateVersion7()) "EXPIRY-ERASED"
        let erased = Service.execute service erasedInput token |> sync |> accepted

        let cleared =
            Service.execute
                service
                (Inputs.caseOperation
                    {
                        OperationId = Guid.CreateVersion7()
                        CaseReference = "EXPIRY-ERASED"
                        ExpectedRevision = 1L
                        Act = AuthoredCaseAct.Bare BareAct.Erase
                    })
                token
            |> sync
            |> accepted

        let old = oldIdentity ()

        Expect.equal
            (lookup service old token)
            (Outcome.Succeeded OperationLookupResult.NotAccepted)
            "Final absence is confirmed through the real execution lock"

        let committed = frontier database |> Option.get

        Expect.isTrue
            (OperationId.issuedAt (CaseFixtures.operationId old) < committed)
            "Independent connection observes committed expiry before reply consumption"

        let mutable writes = 0

        let readOnlyStore =
            { new IStore with
                member _.Context query = fixture.Store.Context query
                member _.Read query = fixture.Store.Read query

                member _.Write query =
                    Interlocked.Increment(&writes) |> ignore
                    fixture.Store.Write query
            }

        let readOnly = { service with Store = readOnlyStore }

        Expect.equal
            (lookup readOnly old token)
            (Outcome.Succeeded OperationLookupResult.NotAccepted)
            "Committed frontier alone establishes final absence"

        Expect.equal writes 0 "Read-only lookup never republishes a cutoff"

        let future =
            fixture.Store.Write
                {
                    Work =
                        fun tx ->
                            task {
                                let! raised = tx.AdvanceExpiryFrontier(tx.ObservedAt.AddMinutes 1)
                                return Verdict.Proceed raised
                            }
                    CancellationToken = token
                }
            |> sync
            |> Result.defaultWith (fun error -> failtestf "%A" error)

        let future =
            match future with
            | Verdict.Proceed value -> value
            | other -> failtestf "%A" other

        let current = Guid.CreateVersion7()

        let observed =
            fixture.Store.Read
                {
                    Work = (fun tx -> Task.FromResult tx.ObservedAt)
                    CancellationToken = token
                }
            |> sync
            |> Result.defaultWith (fun error -> failtestf "%A" error)

        Expect.isTrue
            (OperationLifetime.admits observed (CaseFixtures.operationId current))
            "Negative control: ordinary wall-window admission would allow this ID"

        let target = CaseSelector.Reference "EXPIRY-AVAILABLE"

        let inputs =
            [
                openCase current "EXPIRY-REFUSED"
                OperationInput.Work
                    {
                        OperationId = Guid.CreateVersion7()
                        Target = target
                        ExpectedRevision = 0L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Responsible = WorkValue.Keep
                                    Summary = WorkValue.Set "Not saved"
                                }
                    }
                OperationInput.Note
                    {
                        OperationId = Guid.CreateVersion7()
                        Target = target
                        Act = AuthoredNoteAct.AddNote "Not saved"
                    }
                OperationInput.Reminder
                    {
                        OperationId = Guid.CreateVersion7()
                        Target = target
                        Act =
                            AuthoredReminderAct.Create
                                {
                                    Action = "Not saved"
                                    Due = "2027-01-01"
                                }
                    }
                OperationInput.People(
                    Inputs.peopleRequest
                        (Guid.CreateVersion7())
                        fixture.Owner
                        (PeopleAct.SetProfile Inputs.actorProfile)
                )
            ]

        for input in inputs do
            match (Service.execute service input token |> sync).Outcome with
            | ExecuteResult.Rejected(Refusal.OperationClockUnavailable(cutoff, _)) ->
                Expect.equal cutoff future "Each actual command consults committed frontier"
            | other -> failtestf "Current-window unseen operation must refuse: %A" other

            match (Service.preview service input token |> sync).Outcome with
            | Outcome.Rejected(Refusal.OperationClockUnavailable(cutoff, _)) ->
                Expect.equal cutoff future "Preview consults same frontier"
            | other -> failtestf "Preview must refuse expired unseen operation: %A" other

        let replay = Service.execute service original token |> sync |> replayReceipt

        Expect.equal
            replay.Position
            acceptedReceipt.Position
            "Exact accepted replay precedes frontier"

        for receipt in [ acceptedReceipt; erased ] do
            match lookup service (OperationId.value receipt.OperationId) token with
            | Outcome.Succeeded(OperationLookupResult.Accepted(actual, true)) ->
                Expect.equal
                    actual.Position
                    receipt.Position
                    "Acceptance survives expiry and private erasure"
            | other -> failtestf "Known acceptance must not become absence: %A" other

        let unknown =
            PrincipalKey.human "https://issuer.example/" "expiry-unknown"
            |> Result.defaultWith (string >> failtest)

        let denied = core fixture unknown

        match
            (Service.execute denied (openCase (Guid.CreateVersion7()) "EXPIRY-HIDDEN") token
             |> sync)
                .Outcome
        with
        | ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable) -> ()
        | other -> failtestf "Clock diagnostic must not precede account privacy: %A" other

        Expect.equal (frontier database) (Some future) "Refusals and replays never lower frontier"

        Expect.equal
            (Verification.verify database.OwnerSource None token |> sync)
            (VerificationResult.Verified cleared.Position)
            "Frontier updates and refusals append no business root"
    finally
        database.Fixture.Dispose()

let private cancellation () =
    let database = PostgresFixture.create () |> sync

    try
        let fixture = database.Fixture
        let service = owner fixture
        use cancelled = new CancellationTokenSource()
        let mutable sawAdvance = false

        let interruptedStore =
            { new IStore with
                member _.Context query = fixture.Store.Context query
                member _.Read query = fixture.Store.Read query

                member _.Write query =
                    fixture.Store.Write
                        { query with
                            Work =
                                fun tx ->
                                    task {
                                        let! result = query.Work tx
                                        let! local = tx.ExpiredBefore()
                                        sawAdvance <- local.IsSome
                                        cancelled.Cancel()
                                        return result
                                    }
                        }
            }

        let interrupted =
            { service with
                Store = interruptedStore
            }

        match lookup interrupted (oldIdentity ()) cancelled.Token with
        | Outcome.Failed CoreFault.StoreUnavailable -> ()
        | other -> failtestf "Cancelled confirmation cannot report final absence: %A" other

        Expect.isTrue sawAdvance "Cancellation reaches the post-advance precommit boundary"
        Expect.isNone (frontier database) "Cancelled confirmation rolls back cutoff"
    finally
        database.Fixture.Dispose()

let private acknowledgementLoss () =
    let database = PostgresFixture.create () |> sync

    try
        let fixture = database.Fixture
        let service = owner fixture
        let mutable committed = false

        let interruptedStore =
            { new IStore with
                member _.Context query = fixture.Store.Context query
                member _.Read query = fixture.Store.Read query

                member _.Write query =
                    task {
                        let! result = fixture.Store.Write query

                        match result with
                        | Ok(Verdict.Proceed _) ->
                            committed <- true
                            return Error StoreFailure.CommitOutcomeUnknown
                        | _ -> return result
                    }
            }

        let interrupted =
            { service with
                Store = interruptedStore
            }

        let identity = oldIdentity ()

        match lookup interrupted identity token with
        | Outcome.Failed CoreFault.StoreUnavailable -> ()
        | other -> failtestf "Lost acknowledgement cannot report final absence: %A" other

        Expect.isTrue committed "Actual transaction committed before simulated acknowledgement loss"
        Expect.isSome (frontier database) "Committed frontier survives lost acknowledgement"

        Expect.equal
            (lookup service identity token)
            (Outcome.Succeeded OperationLookupResult.NotAccepted)
            "Later lookup establishes durable final absence"
    finally
        database.Fixture.Dispose()

[<Tests>]
let tests =
    testList
        "expiry confirmation"
        [
            testCase
                "durable absence, all-subject admission and accepted/erased acceptance"
                confirmed
            testCase "precommit cancellation publishes no absence" cancellation
            testCase "commit acknowledgement loss requires a later lookup" acknowledgementLoss
        ]
