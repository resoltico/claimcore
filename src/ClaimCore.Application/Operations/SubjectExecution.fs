namespace ClaimCore.Application

open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal SubjectExecutionMode =
    | Preview
    | Commit of LedgerWriteContext

[<NoEquality; NoComparison>]
type internal OperationRegistration<'input, 'facts, 'decision, 'rows> =
    {
        LoadAndDecide:
            SubjectExecutionMode -> IReadTx -> 'input -> Task<'facts * Verdict<'decision>>
        Actor: 'facts -> AccessView
        Append: 'input -> 'facts -> 'decision -> LedgerAppend<'rows>
        Project: IWriteTx -> 'rows -> Task<unit>
        Review: 'input -> 'facts -> 'decision -> OperationReview
    }

/// One transaction/append/projection path serves every subject registration.
module internal SubjectExecution =
    let isCommit =
        function
        | SubjectExecutionMode.Preview -> false
        | SubjectExecutionMode.Commit _ -> true

    let accept services actor write append proposed =
        proposed
        |> Verdict.bind (fun decision ->
            LedgerEnvelope.accept services actor write (append decision))

    let inTransaction services registration input (tx: IWriteTx) =
        task {
            let write =
                {
                    Head = tx.Head
                    NewPayloadNonce = tx.NewPayloadNonce
                }

            let! facts, proposed =
                registration.LoadAndDecide (SubjectExecutionMode.Commit write) tx input

            let accepted =
                accept
                    services
                    (registration.Actor facts)
                    write
                    (registration.Append input facts)
                    proposed

            return! ExecutionFacts.persist tx (registration.Project tx) accepted
        }

    let transact (services: ServiceContext) work token =
        services.Store.Write
            {
                Work = work
                CancellationToken = token
            }

    let snapshot (services: ServiceContext) work token =
        services.Store.Read
            {
                Work = work
                CancellationToken = token
            }

    let run services registration input token =
        transact services (inTransaction services registration input) token

    let inSnapshot registration input tx =
        task {
            let! facts, proposed =
                registration.LoadAndDecide SubjectExecutionMode.Preview tx input

            return
                proposed
                |> Verdict.outcome
                    (fun decision -> Previewed.Reviewed(registration.Review input facts decision))
                    Previewed.AlreadyAccepted
        }

    let preview (services: ServiceContext) registration input (token: CancellationToken) =
        task {
            if token.IsCancellationRequested then
                return Outcome.Failed CoreFault.StoreUnavailable
            else
                let! result = snapshot services (inSnapshot registration input) token
                return QueryResults.readResult result
        }
