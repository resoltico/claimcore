namespace ClaimCore.Database

open System
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open System.Threading
open ClaimCore.Postgres

module Execution =
    let private perform (administration: Administration) command =
        task {
            match command with
            | Initialize zone ->
                let! id = administration.Initialize(zone, CancellationToken.None)

                return Replies.initialized id
            | ProvisionOwner ->
                let operationId, principal = Inputs.owner ()

                let! result =
                    administration.ProvisionOwner(operationId, principal, CancellationToken.None)

                return Replies.authority result
            | Verify expected ->
                let started = System.Diagnostics.Stopwatch.GetTimestamp()
                let! result = administration.Verify(expected, CancellationToken.None)

                return
                    Replies.verification
                        (int64 (
                            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds
                        ))
                        result
            | Rebuild expected ->
                let! result = administration.Rebuild(expected, CancellationToken.None)
                return Replies.rebuild result
            | InvalidateRestoredSessions ->
                let! count = administration.InvalidateRestoredSessions(CancellationToken.None)
                return Replies.sessionsInvalidated count
            | Status ->
                let! status = administration.Status(CancellationToken.None)
                return Replies.status status
            | Maintenance change ->
                let! result = administration.Maintenance(change, CancellationToken.None)
                return Replies.maintenance result
            | PruneSessions limit ->
                let! result = administration.PruneExpiredSessions(limit, CancellationToken.None)
                return Replies.sessionsPruned result
            | Head ->
                let! head = administration.Head(CancellationToken.None)

                return Replies.head head
            | Help
            | Version -> return invalidOp "Discovery does not open administration."
        }

    let run command =
        task {
            match command with
            | Help -> return Replies.help ()
            | Version -> return Replies.version ()
            | _ ->
                try
                    use administration =
                        new Administration(Inputs.connection (), BuildIdentity.current.Version)

                    return! perform administration command
                with
                | :? MaintenanceRequiredException -> return Replies.maintenanceRequired ()
                | error ->
                    return
                        match AdministrationFailures.classify error with
                        | AdministrationFailure.Refused ->
                            Replies.rejected (Refusal.Access AccessRefusal.ActNotPermitted)
                        | AdministrationFailure.OutcomeUnknown -> Replies.unknown None
                        | AdministrationFailure.Failed -> Replies.failed CoreFault.StoreUnavailable
        }
