module ClaimCore.Tests.ServiceBoundaryTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Tests.DecisionFixtures

let private unavailable () =
    let store = new ClaimCore.TestSupport.MemoryStore.Store(instant)
    store.ReadFailure <- Some StoreFailure.Unavailable
    store.WriteFailure <- Some StoreFailure.Unavailable
    store

let private inputs () =
    [
        OperationInput.Case(opening 470 "BOUNDARY")
        OperationInput.Authority(authorityRequest (id 471) other AuthorityAct.Register)
    ]

let private malformed () =
    async {
        use probe = unavailable ()

        let core =
            Service.create
                BuildIdentity.current.Version
                installation
                (probe :> IStore)
                calendar
                owner

        let invalid =
            [
                OperationInput.Case
                    { opening 470 "BOUNDARY" with
                        OperationId = Guid.Empty
                    }
                OperationInput.Case(
                    actRequest 471 "BOUNDARY" 1L (AuthoredCaseAct.Void Unchecked.defaultof<string>)
                )
                OperationInput.Authority(authorityRequest Guid.Empty other AuthorityAct.Register)
            ]

        for input in invalid do
            let! execute =
                (Service.execute core (input) (CancellationToken.None)) |> Async.AwaitTask

            let! preview =
                (Service.preview core (input) (CancellationToken.None)) |> Async.AwaitTask

            Expect.isTrue
                (match execute.Outcome with
                 | ExecuteResult.Rejected _ -> true
                 | _ -> false)
                "Invalid execution refused"

            Expect.isTrue
                (match preview.Outcome with
                 | PreviewResult.Rejected _ -> true
                 | _ -> false)
                "Invalid preview refused"

        Expect.equal probe.Calls 0 "Admission performs no port work"
    }

let private failures () =
    async {
        for failure, expected in
            [
                StoreFailure.Unavailable, ExecuteResult.Failed CoreFault.StoreUnavailable
                StoreFailure.Cancelled, ExecuteResult.Failed CoreFault.StoreUnavailable
                StoreFailure.Internal, ExecuteResult.Failed CoreFault.InternalError
                StoreFailure.CommitOutcomeUnknown, ExecuteResult.OutcomeUnknown
            ] do
            use probe = unavailable ()
            probe.WriteFailure <- Some failure

            let core =
                Service.create
                    BuildIdentity.current.Version
                    installation
                    (probe :> IStore)
                    calendar
                    owner

            for input in inputs () do
                let! reply =
                    (Service.execute core (input) (CancellationToken.None)) |> Async.AwaitTask

                Expect.equal
                    reply.OperationId
                    (OperationInput.operationId input)
                    "Failure retains identity"

                Expect.equal reply.Outcome expected "Only dispatched commit uncertainty is unknown"

            Expect.equal probe.Calls 2 "Each write dispatched once"
    }

let private cancelled () =
    async {
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()
        use probe = unavailable ()

        let core =
            Service.create
                BuildIdentity.current.Version
                installation
                (probe :> IStore)
                calendar
                owner

        for input in inputs () do
            let! execute = (Service.execute core (input) (cancellation.Token)) |> Async.AwaitTask

            let! preview = (Service.preview core (input) (cancellation.Token)) |> Async.AwaitTask

            Expect.equal
                execute.OperationId
                (OperationInput.operationId input)
                "Cancelled write identity"

            Expect.equal
                execute.Outcome
                (ExecuteResult.Failed CoreFault.StoreUnavailable)
                "Definite predispatch failure"

            Expect.equal
                preview.Outcome
                (PreviewResult.Failed CoreFault.StoreUnavailable)
                "Cancelled preview"

        Expect.equal probe.Calls 0 "No writes or previews dispatched"

        let! context = (Service.context core () cancellation.Token) |> Async.AwaitTask

        let! current =
            (Service.get core ({ CaseReference = "BOUNDARY" }) (cancellation.Token))
            |> Async.AwaitTask

        let! observed =
            (Service.observe core ({ OperationId = id 470 }) (cancellation.Token))
            |> Async.AwaitTask

        Expect.equal context (ReadReply.Failed CoreFault.StoreUnavailable) "Cancelled context"
        Expect.equal current (ReadReply.Failed CoreFault.StoreUnavailable) "Cancelled case"
        Expect.equal observed (ReadReply.Failed CoreFault.StoreUnavailable) "Cancelled observation"
    }

let private committed () =
    async {
        for input in inputs () do
            use memory = new ClaimCore.TestSupport.MemoryStore.Store(instant)

            memory.SeedActor
                worker.Actor.Value
                (worker.Installation |> List.map (fun role -> role, None))

            use cancellation = new CancellationTokenSource()
            let inner = memory :> IStore

            let probe =
                { new IStore with
                    member _.Read
                        ({
                             ReadOperation.Work = work
                             CancellationToken = ct
                         })
                        =
                        inner.Read(
                            {
                                ReadOperation.Work = work
                                CancellationToken = ct
                            }
                        )

                    member _.Write
                        ({
                             WriteOperation.Work = work
                             CancellationToken = ct
                         })
                        =
                        task {
                            let! result =
                                inner.Write(
                                    {
                                        WriteOperation.Work = work
                                        CancellationToken = ct
                                    }
                                )

                            cancellation.Cancel()
                            return result
                        }
                }

            let core =
                Service.create BuildIdentity.current.Version installation probe calendar owner

            let! reply = (Service.execute core (input) (cancellation.Token)) |> Async.AwaitTask

            let! observed =
                Service.observe
                    core
                    {
                        OperationId = OperationInput.operationId input
                    }
                    CancellationToken.None
                |> Async.AwaitTask

            match reply.Outcome, observed with
            | ExecuteResult.Accepted receipt, ReadReply.Succeeded stored ->
                Expect.equal receipt stored "Completed acceptance is forwarded despite cancellation"
            | _ -> failtest "Committed acceptance and stored receipt must agree"

            Expect.isTrue
                cancellation.IsCancellationRequested
                "Cancellation occurred at port completion"
    }

[<Tests>]
let tests =
    testList
        "Core port boundaries"
        [
            testCaseAsync
                "malformed business stewardship and authority input perform no port work"
                (malformed ())
            testCaseAsync "write failures preserve identity and dispatch uncertainty" (failures ())
            testCaseAsync
                "predispatch cancellation and cancelled reads remain definite"
                (cancelled ())
            testCaseAsync
                "completed acceptance survives cancellation before delivery"
                (committed ())
        ]
