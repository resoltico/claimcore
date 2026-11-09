module ClaimCore.IntegrationTests.OperationLookupTests

open System
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.Postgres
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

/// The provider still owns locking, reads, writes and commit; only its clock input is controlled.
type ClockTransaction() =
    inherit DispatchProxy()
    member val Inner: IReadTx = Unchecked.defaultof<_> with get, set
    member val Instant = DateTimeOffset.MinValue with get, set

    override this.Invoke(methodInfo, arguments) =
        let methodInfo = nonNull methodInfo

        if methodInfo.Name = "get_ObservedAt" then
            box this.Instant
        else
            methodInfo.Invoke(this.Inner, arguments)

let private withClock<'Tx when 'Tx :> IReadTx> instant (tx: 'Tx) =
    let proxy = DispatchProxy.Create<'Tx, ClockTransaction>()
    let clock = nonNull (box proxy) :?> ClockTransaction
    clock.Inner <- tx :> IReadTx
    clock.Instant <- instant
    proxy

type ControlledStore
    (
        inner: IStore,
        instant,
        finished: TaskCompletionSource<unit> option,
        release: Task option,
        read: TaskCompletionSource<unit> option,
        ?readInstant: DateTimeOffset
    ) =
    interface IStore with
        member _.Context query = inner.Context query

        member _.Read operation =
            task {
                let! result =
                    inner.Read
                        { operation with
                            Work =
                                fun tx ->
                                    let current =
                                        readInstant
                                        |> Option.map (fun instant -> withClock instant tx)
                                        |> Option.defaultValue tx

                                    operation.Work current
                        }

                read |> Option.iter (fun signal -> signal.TrySetResult() |> ignore)
                return result
            }

        member _.Write operation =
            inner.Write
                { operation with
                    Work =
                        fun tx ->
                            task {
                                let tx =
                                    instant
                                    |> Option.map (fun value -> withClock value tx)
                                    |> Option.defaultValue tx

                                let! result = operation.Work tx

                                finished
                                |> Option.iter (fun signal -> signal.TrySetResult() |> ignore)

                                match release with
                                | Some gate -> do! gate
                                | None -> ()

                                return result
                            }
                }

let private owner fixture =
    task {
        let service = core fixture fixture.Owner

        let! provision =
            AuthorityExecution.provisionStore
                service
                (OperationIds.fresh ())
                Inputs.ownerProfile
                []
                CancellationToken.None

        match provision with
        | Ok(Verdict.Proceed _) -> return service
        | other -> return failtestf "Owner provisioning: %A" other
    }

let private lookup service operation =
    task {
        let! reply =
            Service.operationGet service { OperationId = operation } CancellationToken.None

        return reply.Outcome
    }

let private request operation =
    OperationInput.Authority(
        Inputs.authorityRequest
            operation
            (PrincipalKey.human "https://issuer.example/" "lookup-subject"
             |> Result.defaultWith (string >> invalidOp))
            (AuthorityAct.Register Inputs.ownerProfile)
    )

[<Tests>]
let tests =
    testList
        "Bounded operation lookup"
        [
            testTask "absence is pending through the deadline and final after expiry without append" {
                return!
                    task {
                        let! db = PostgresFixture.create ()

                        use _owned =
                            { new IDisposable with
                                member _.Dispose() = db.Fixture.Dispose()
                            }

                        let! service = owner db.Fixture
                        let issued = DateTimeOffset.UtcNow.AddMinutes(-11.)
                        let pendingId = OperationIds.at issued

                        let beforeExpiry =
                            ControlledStore(
                                db.Fixture.Store,
                                None,
                                None,
                                None,
                                None,
                                readInstant = issued.AddMinutes(9.)
                            )

                        let! pending = lookup { service with Store = beforeExpiry } pendingId

                        match pending with
                        | Outcome.Succeeded(OperationLookupResult.Pending finalAt) ->
                            Expect.equal
                                finalAt
                                (issued
                                    .AddMinutes(10.)
                                    .AddTicks(-(issued.Ticks % TimeSpan.TicksPerMillisecond)))
                                "Exact UUID timestamp deadline"
                        | other -> failtestf "Expected pending: %A" other

                        let! before =
                            LedgerAdministration.head db.OwnerSource CancellationToken.None

                        let! final = lookup service pendingId

                        match final with
                        | Outcome.Succeeded OperationLookupResult.NotAccepted -> ()
                        | other -> failtestf "Expected final absence: %A" other

                        let! after = LedgerAdministration.head db.OwnerSource CancellationToken.None
                        Expect.equal after before "Confirmation appends no ledger node"
                    }
            }
            testTask "first execution rejects expired and future IDs while replay survives expiry" {
                return!
                    task {
                        let! db = PostgresFixture.create ()

                        use _owned =
                            { new IDisposable with
                                member _.Dispose() = db.Fixture.Dispose()
                            }

                        let! service = owner db.Fixture

                        for stamp in
                            [
                                DateTimeOffset.UtcNow.AddMinutes(-11.)
                                DateTimeOffset.UtcNow.AddMinutes(3.)
                            ] do
                            let! (refused: ExecuteReply) =
                                Service.execute
                                    service
                                    (request (OperationIds.at stamp))
                                    CancellationToken.None

                            match refused.Outcome with
                            | ExecuteResult.Rejected(Refusal.OperationIdOutOfWindow _) -> ()
                            | other -> failtestf "Expected lifetime refusal: %A" other

                        let operation = OperationIds.fresh ()
                        let input = request operation

                        let! (accepted: ExecuteReply) =
                            Service.execute service input CancellationToken.None

                        match accepted.Outcome with
                        | ExecuteResult.Accepted _ -> ()
                        | other -> failtestf "Expected acceptance: %A" other

                        let late =
                            ControlledStore(
                                db.Fixture.Store,
                                Some(DateTimeOffset.UtcNow.AddMinutes(11.)),
                                None,
                                None,
                                None
                            )

                        let! (replay: ExecuteReply) =
                            Service.execute
                                { service with Store = late }
                                input
                                CancellationToken.None

                        match replay.Outcome with
                        | ExecuteResult.AlreadyAccepted _ -> ()
                        | other -> failtestf "Expected replay before window check: %A" other
                    }
            }
            testTask "locked confirmation sees an in-flight accepted writer" {
                return!
                    task {
                        let! db = PostgresFixture.create ()

                        use _owned =
                            { new IDisposable with
                                member _.Dispose() = db.Fixture.Dispose()
                            }

                        let! service = owner db.Fixture
                        let issued = DateTimeOffset.UtcNow
                        let operation = OperationIds.at issued

                        let prepared =
                            TaskCompletionSource<unit>(
                                TaskCreationOptions.RunContinuationsAsynchronously
                            )

                        let release =
                            TaskCompletionSource<unit>(
                                TaskCreationOptions.RunContinuationsAsynchronously
                            )

                        let read =
                            TaskCompletionSource<unit>(
                                TaskCreationOptions.RunContinuationsAsynchronously
                            )

                        let writer =
                            ControlledStore(
                                db.Fixture.Store,
                                None,
                                Some prepared,
                                Some release.Task,
                                None
                            )

                        let executing =
                            Service.execute
                                { service with Store = writer }
                                (request operation)
                                CancellationToken.None

                        do! prepared.Task.WaitAsync(TimeSpan.FromSeconds 10.)
                        let finalInstant = issued.AddMinutes(11.)

                        let reader =
                            ControlledStore(
                                db.Fixture.Store,
                                Some finalInstant,
                                None,
                                None,
                                Some read,
                                readInstant = finalInstant
                            )

                        let getting = lookup { service with Store = reader } operation
                        do! read.Task.WaitAsync(TimeSpan.FromSeconds 10.)
                        do! Task.Delay 50

                        Expect.isFalse
                            getting.IsCompleted
                            "An absence answer must wait behind the ledger head"

                        release.SetResult()

                        let! (executed: ExecuteReply) =
                            executing.WaitAsync(TimeSpan.FromSeconds 10.)

                        match executed.Outcome with
                        | ExecuteResult.Accepted _ -> ()
                        | other -> failtestf "Writer should accept: %A" other

                        let! result = getting.WaitAsync(TimeSpan.FromSeconds 10.)

                        match result with
                        | Outcome.Succeeded(OperationLookupResult.Accepted(receipt, true)) ->
                            Expect.equal
                                (OperationId.value receipt.OperationId)
                                operation
                                "Locked confirmation observes committed receipt"
                        | other -> failtestf "Expected accepted after concurrent commit: %A" other
                    }
            }
            testTask "confirmation rechecks the locked clock before declaring absence final" {
                return!
                    task {
                        let! db = PostgresFixture.create ()

                        use _owned =
                            { new IDisposable with
                                member _.Dispose() = db.Fixture.Dispose()
                            }

                        let! service = owner db.Fixture
                        let issued = DateTimeOffset.UtcNow
                        let operation = OperationIds.at issued

                        let confirmation =
                            ControlledStore(
                                db.Fixture.Store,
                                Some(issued.AddMinutes(9.)),
                                None,
                                None,
                                None
                            )

                        let! result = lookup { service with Store = confirmation } operation

                        match result with
                        | Outcome.Succeeded(OperationLookupResult.Pending finalAt) ->
                            Expect.equal
                                finalAt
                                (OperationLifetime.finalAt (CaseFixtures.operationId operation))
                                "A reopened window cannot receive a final absence answer"
                        | other -> failtestf "Locked clock must still prove expiry: %A" other
                    }
            }
            testTask "hidden foreign case content is not loaded when checking own absence" {
                return!
                    task {
                        let! db = PostgresFixture.create ()

                        use _owned =
                            { new IDisposable with
                                member _.Dispose() = db.Fixture.Dispose()
                            }

                        let! service = caseWorker db.Fixture

                        let observer =
                            PrincipalKey.human "https://issuer.example/" "hidden-lookup-reader"
                            |> Result.defaultWith (string >> invalidOp)

                        let! registered =
                            Service.execute
                                service
                                (OperationInput.Authority(
                                    Inputs.authorityRequest
                                        (OperationIds.fresh ())
                                        observer
                                        (AuthorityAct.Register Inputs.actorProfile)
                                ))
                                CancellationToken.None

                        acceptedAuthority registered
                        let operation = OperationIds.fresh ()

                        let! opened =
                            Service.execute
                                service
                                (Inputs.caseOperation
                                    {
                                        OperationId = operation
                                        CaseReference = "HIDDEN-CONTENT"
                                        ExpectedRevision = 0L
                                        Act = AuthoredCaseAct.Open registration
                                    })
                                CancellationToken.None

                        accepted opened |> ignore
                        let reader = core db.Fixture observer
                        let! hiddenBefore = lookup reader operation

                        let deadline =
                            match hiddenBefore with
                            | Outcome.Succeeded(OperationLookupResult.Pending finalAt) -> finalAt
                            | other ->
                                failtestf
                                    "A foreign acceptance must be invisible to this caller: %A"
                                    other

                        let! visibleBefore = lookup service operation

                        match visibleBefore with
                        | Outcome.Succeeded(OperationLookupResult.Accepted(_, true)) -> ()
                        | other -> failtestf "The author can read the valid receipt: %A" other

                        use! connection = db.OwnerSource.OpenConnectionAsync()

                        let! changed =
                            DatabaseSession.execute
                                connection
                                "UPDATE claimcore.cases SET snapshot=decode('00','hex') WHERE case_id=$1"
                                [| box operation |]
                                CancellationToken.None

                        Expect.equal
                            changed
                            1
                            "The corruption control changes the real case projection"

                        let! hiddenAfter = lookup reader operation

                        match hiddenAfter with
                        | Outcome.Succeeded(OperationLookupResult.Pending finalAt) ->
                            Expect.equal
                                finalAt
                                deadline
                                "Hidden content corruption cannot reveal acceptance or change the deadline"
                        | other -> failtestf "Hidden case content must not be decoded: %A" other

                        let! visibleAfter = lookup service operation

                        match visibleAfter with
                        | Outcome.Failed CoreFault.StoreIntegrityError -> ()
                        | other ->
                            failtestf
                                "The author independently detects the corrupted projection: %A"
                                other
                    }
            }
        ]
