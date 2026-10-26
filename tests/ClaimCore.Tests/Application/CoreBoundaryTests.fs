module ClaimCore.Tests.CoreBoundaryTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Tests.DecisionFixtures

// This port probe never evaluates a decision or stores accepted state.
type private PortProbe
    (write: CancellationToken -> Task<Result<Decision<OperationReceipt>, StoreFailure>>) =
    let mutable calls = 0
    member _.Calls = calls

    member private _.Unavailable(ct: CancellationToken) =
        calls <- calls + 1

        Task.FromResult(
            Error(
                if ct.IsCancellationRequested then
                    StoreFailure.Cancelled
                else
                    StoreFailure.Unavailable
            )
        )

    interface ILedgerStore with
        member this.ResolveOperation(_, _, _, ct) = this.Unavailable ct

        member _.WriteCase(_, _, _, _, ct) =
            calls <- calls + 1
            write ct

        member _.WriteAuthority(_, _, _, _, _, ct) =
            calls <- calls + 1
            write ct

        member this.ReadAuthorityTarget(_, _, _, _, ct) = this.Unavailable ct
        member this.ReadCase(_, _, _, ct) = this.Unavailable ct
        member this.ReadActor(_, ct) = this.Unavailable ct
        member this.ReadOperation(_, _, ct) = this.Unavailable ct
        member this.ListCases(_, _, _, _, ct) = this.Unavailable ct
        member this.ReadHistory(_, _, _, _, ct) = this.Unavailable ct
        member this.ListGrants(_, _, _, _, ct) = this.Unavailable ct
        member this.ListActors(_, _, _, ct) = this.Unavailable ct

let private inputs () =
    [
        OperationInput.Case(opening 470 "BOUNDARY")
        OperationInput.Authority(authorityRequest (id 471) other AdmittedAuthorityAct.Register)
    ]

let private malformed () =
    async {
        let probe = PortProbe(fun _ -> failtest "Invalid work reached storage")
        let core = Handlers.context installation (probe :> ILedgerStore) calendar owner

        let invalid =
            [
                OperationInput.Case
                    { opening 470 "BOUNDARY" with
                        OperationId = Guid.Empty
                    }
                OperationInput.Case(
                    actRequest
                        471
                        "BOUNDARY"
                        1L
                        (UnvalidatedCaseAct.Void Unchecked.defaultof<string>)
                )
                OperationInput.Authority(
                    authorityRequest Guid.Empty other AdmittedAuthorityAct.Register
                )
            ]

        for input in invalid do
            let! execute =
                (Core.execute core (input) (CancellationToken.None)) |> Async.AwaitTask

            let! preview =
                (Core.previewReply core (input) (CancellationToken.None)) |> Async.AwaitTask

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
            let probe = PortProbe(fun _ -> Task.FromResult(Error failure))
            let core = Handlers.context installation (probe :> ILedgerStore) calendar owner

            for input in inputs () do
                let! reply = (Core.execute core (input) (CancellationToken.None)) |> Async.AwaitTask

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
        let probe = PortProbe(fun _ -> failtest "Predispatch cancellation reached write")
        let core = Handlers.context installation (probe :> ILedgerStore) calendar owner

        for input in inputs () do
            let! execute = (Core.execute core (input) (cancellation.Token)) |> Async.AwaitTask

            let! preview =
                (Core.previewReply core (input) (cancellation.Token)) |> Async.AwaitTask

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

        let! context = (Core.context core () cancellation.Token) |> Async.AwaitTask

        let! current =
            (Core.get core ({ CaseReference = "BOUNDARY" }) (cancellation.Token))
            |> Async.AwaitTask

        let! observed =
            (Core.observe core ({ OperationId = id 470 }) (cancellation.Token))
            |> Async.AwaitTask

        Expect.equal context (ReadReply.Failed CoreFault.StoreUnavailable) "Cancelled context"
        Expect.equal current (ReadReply.Failed CoreFault.StoreUnavailable) "Cancelled case"
        Expect.equal observed (ReadReply.Failed CoreFault.StoreUnavailable) "Cancelled observation"
    }

let private committed () =
    async {
        for input in inputs () do
            let receipt =
                match input with
                | OperationInput.Case request ->
                    let _, _, receipt = decide request (locked worker None None) |> appended
                    receipt
                | OperationInput.Authority input ->
                    let request = AuthorityRequests.admit input |> Fixtures.accepted

                    match
                        AuthorityDecisions.execute
                            installation
                            owner
                            request
                            (authorityView worker AuthorityView.empty None)
                    with
                    | Decision.Proceed(_, _, receipt) -> receipt
                    | _ -> failtest "Expected accepted authority fixture"

            use cancellation = new CancellationTokenSource()

            let probe =
                PortProbe(fun _ ->
                    cancellation.Cancel()
                    Task.FromResult(Ok(Decision.Proceed receipt)))

            let core = Handlers.context installation (probe :> ILedgerStore) calendar owner

            let! reply = (Core.execute core (input) (cancellation.Token)) |> Async.AwaitTask

            Expect.equal
                reply.Outcome
                (ExecuteResult.Accepted receipt)
                "Completed acceptance is forwarded despite cancellation"

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
