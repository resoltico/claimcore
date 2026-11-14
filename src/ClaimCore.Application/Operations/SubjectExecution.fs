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
        Load: IReadTx -> 'input -> Task<'facts>
        Context: SubjectExecutionMode -> 'facts -> DecisionContext
        Decide:
            SubjectExecutionMode
                -> DecisionContext
                -> IReadTx
                -> 'input
                -> 'facts
                -> Task<Verdict<'decision>>
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

    let context
        (services: ServiceContext)
        mode
        observedAt
        access
        (maintenance: RegisterMaintenance)
        frontier
        =
        {
            DecisionContext.BusinessDate = Some(services.Calendar.DateAt observedAt)
            ObservedAt = observedAt
            Principal = services.Principal
            Access = access
            Maintenance = maintenance.Active
            Lifetime =
                if isCommit mode then
                    DecisionLifetime.Execution frontier
                else
                    DecisionLifetime.Preview frontier
        }

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

            try
                let mode = SubjectExecutionMode.Commit write
                let! facts = registration.Load tx input
                let context = registration.Context mode facts
                let! proposed = registration.Decide mode context tx input facts

                let accepted =
                    accept services context.Access write (registration.Append input facts) proposed

                return! ExecutionFacts.persist tx (registration.Project tx) accepted
            with SubjectStateInvalid _ ->
                return Verdict.Fail CoreFault.StoreIntegrityError
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
            try
                let mode = SubjectExecutionMode.Preview
                let! facts = registration.Load tx input
                let context = registration.Context mode facts
                let! proposed = registration.Decide mode context tx input facts

                return
                    proposed
                    |> Verdict.outcome
                        (fun decision ->
                            Previewed.Reviewed(registration.Review input facts decision))
                        Previewed.AlreadyAccepted
            with SubjectStateInvalid _ ->
                return Outcome.Failed CoreFault.StoreIntegrityError
        }

    let preview (services: ServiceContext) registration input (token: CancellationToken) =
        task {
            if token.IsCancellationRequested then
                return Outcome.Failed CoreFault.StoreUnavailable
            else
                let! result = snapshot services (inSnapshot registration input) token
                return QueryResults.readResult result
        }
