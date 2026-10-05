module ClaimCore.Tests.ScriptedClaimStore

open System
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.TestSupport

type internal ExecutionMode =
    | Normal
    | Throws
    | Unknown
    | FailsBeforeCommit

type internal Store(mode: ExecutionMode) =
    let inner = new CoreStore.Store()
    member _.TransactionCalls = inner.TransactionCalls

    interface ITestCommandExecutor with
        member _.Execute(operation, capture, decide) =
            match mode with
            | Normal -> (inner :> ITestCommandExecutor).Execute(operation, capture, decide)
            | Throws ->
                Task.FromException<Result<Receipt, CoreFailure>>(InvalidOperationException())
            | Unknown ->
                let operationId = (Operation.request operation).OperationId
                Task.FromResult(Error(CoreFailure.CommitOutcomeUnknown operationId))
            | FailsBeforeCommit -> Task.FromResult(Error CoreFailure.StoreUnavailable)

    interface IClaimStore with
        member _.Get(reference, ct) =
            (inner :> IClaimStore).Get(reference, ct)

        member _.List(after, ct) = (inner :> IClaimStore).List(after, ct)

        member _.History(reference, after, ct) =
            (inner :> IClaimStore).History(reference, after, ct)

        member _.Operation(operationId, ct) =
            (inner :> IClaimStore).Operation(operationId, ct)

        member _.Accepted(operationId, requestSha256, ct) =
            (inner :> IClaimStore).Accepted(operationId, requestSha256, ct)
