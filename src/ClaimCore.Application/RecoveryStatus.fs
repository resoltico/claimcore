namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Actor-scoped recovery reads retained evidence in a read-only snapshot without taking the head lock.
module internal RecoveryStatus =
    let private outcome services observedAt operation actor entry current fenced =
        match Resolution.resolve services observedAt operation actor entry current with
        | Verdict.Replay receipt -> Outcome.Succeeded(OperationStatus.Accepted receipt)
        | Verdict.Proceed _ ->
            Outcome.Succeeded(
                if fenced then
                    OperationStatus.NotAccepted
                else
                    OperationStatus.Unresolved
            )
        | Verdict.Refuse refusal -> Outcome.Rejected refusal
        | Verdict.Fail fault -> Outcome.Failed fault

    let run (services: ServiceContext) (request: OperationLookup) token =
        task {
            match OperationId.create request.OperationId with
            | None ->
                return!
                    RequestRefusals.read
                        services
                        token
                        (Refusal.invalidInput (
                            InputTarget.OperationId,
                            Violation.OperationIdRequired
                        ))
            | Some operation ->
                let! result =
                    services.Store.Read
                        {
                            Work =
                                fun tx ->
                                    task {
                                        let! actor, entry, current =
                                            ExecutionFacts.operation tx services.Principal operation

                                        let! fenced =
                                            ExecutionFacts.fenced services tx actor operation entry

                                        let! named =
                                            ReceiptReads.existing services tx actor current entry

                                        return
                                            match fenced, named with
                                            | Error fault, _
                                            | _, Error fault -> Outcome.Failed fault
                                            | Ok fenced, Ok current ->
                                                outcome
                                                    services
                                                    tx.ObservedAt
                                                    operation
                                                    actor
                                                    entry
                                                    current
                                                    fenced
                                    }
                            CancellationToken = token
                        }

                return QueryResults.readResult result
        }
