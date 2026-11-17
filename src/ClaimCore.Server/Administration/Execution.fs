namespace ClaimCore.Server.Administration

open System
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open System.Threading
open ClaimCore.Application
open ClaimCore.Application.Administration

module Execution =
    let private perform (administration: Administration) token command =
        task {
            match command with
            | Initialize(zone, owner) ->
                let! id = administration.Initialize(zone, owner, token)
                return Replies.initialized id
            | Verify expected ->
                let started = System.Diagnostics.Stopwatch.GetTimestamp()
                let! result = administration.Verify(expected, token)

                return
                    Replies.verification
                        (int64 (
                            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds
                        ))
                        result
            | Rebuild(expected, handle) ->
                let! result = administration.Rebuild(expected, handle, token)
                return Replies.rebuild result
            | AdmitRestoredInstallation(checkpoint, handle) ->
                let! result = administration.AdmitRestoredInstallation(checkpoint, handle, token)

                return Replies.restoreAdmission result
            | Status ->
                let! status = administration.Status(token)
                return Replies.status status
            | Maintenance change ->
                let! result = administration.Maintenance(change, token)
                return Replies.maintenance result
            | Head ->
                let! head = administration.Head(token)

                return Replies.head head
            | Help
            | Version -> return invalidOp "Discovery does not open administration."
        }

    let run token command =
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

                        return! perform administration token command
                    with error ->
                        return
                            match
                                ClaimCore.Application.Administration.Administration.ClassifyFailure
                                    error
                            with
                            | AdministrationFailure.OutcomeUnknown -> Replies.unknown None
                            | AdministrationFailure.Fault fault -> Replies.operatorFault fault
        }
