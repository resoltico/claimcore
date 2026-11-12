namespace ClaimCore.Database

open System
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open System.Threading
open ClaimCore.Postgres

module Execution =
    let private perform (administration: Administration) command =
        task {
            match command with
            | Initialize(zone, owner) ->
                let! id = administration.Initialize(zone, owner, CancellationToken.None)
                return Replies.initialized id
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
            | AdmitRestoredInstallation checkpoint ->
                let! result =
                    administration.AdmitRestoredInstallation(checkpoint, CancellationToken.None)

                return Replies.restoreAdmission result
            | Status ->
                let! status = administration.Status(CancellationToken.None)
                return Replies.status status
            | Maintenance change ->
                let! result = administration.Maintenance(change, CancellationToken.None)
                return Replies.maintenance result
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
                match Inputs.connection () with
                | Error fault -> return Replies.operatorFault fault
                | Ok connection ->
                    try
                        use administration =
                            new Administration(connection, BuildIdentity.current.Version)

                        return! perform administration command
                    with error ->
                        return
                            match AdministrationFailures.classify error with
                            | AdministrationFailure.OutcomeUnknown -> Replies.unknown None
                            | AdministrationFailure.Fault fault -> Replies.operatorFault fault
        }
