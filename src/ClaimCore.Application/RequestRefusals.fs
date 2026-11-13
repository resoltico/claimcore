namespace ClaimCore.Application

open System.Threading
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Early business input failures still belong to a current caller's authorized snapshot.
module internal RequestRefusals =
    let private current (services: ServiceContext) (tx: IReadTx) refusal =
        task {
            let! actor =
                tx.Access
                    {
                        Principal = services.Principal
                        CaseId = None
                    }

            return
                if Access.validActor services.Principal actor then
                    refusal
                else
                    Refusal.Access AccessRefusal.ResourceUnavailable
        }

    let read (services: ServiceContext) (token: CancellationToken) refusal =
        task {
            if token.IsCancellationRequested then
                return Outcome.Failed CoreFault.StoreUnavailable
            else
                let! result =
                    services.Store.Read
                        {
                            CancellationToken = token
                            Work =
                                fun tx ->
                                    task {
                                        let! reason = current services tx refusal
                                        return Outcome.Rejected reason
                                    }
                        }

                return QueryResults.readResult result
        }

    let write (services: ServiceContext) (token: CancellationToken) refusal =
        task {
            if token.IsCancellationRequested then
                return Error StoreFailure.Cancelled
            else
                return!
                    services.Store.Write
                        {
                            CancellationToken = token
                            Work =
                                fun tx ->
                                    task {
                                        let! reason = current services tx refusal
                                        return Verdict.Refuse reason
                                    }
                        }
        }

    let invalidLimit services token =
        read services token QueryResults.invalidLimitRefusal
