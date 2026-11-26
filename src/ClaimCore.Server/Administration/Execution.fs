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
            | Initialize(zone, owner, policy, true) ->
                let! result = administration.InitializeIfMissing(zone, owner, policy, token)

                return
                    match result with
                    | InitializationResult.Initialized id -> Replies.initialized id
                    | InitializationResult.ExistingInstallation id ->
                        Replies.initializationSkipped id
            | Initialize(zone, owner, policy, false) ->
                let! installation = administration.Initialize(zone, owner, policy, token)
                return Replies.initialized installation
            | RecoverOwner input ->
                let! result = administration.RecoverOwner(input, token)
                return Replies.ownerRecovery result
            | RotateMasterKey keyFile ->
                match Inputs.keyFile keyFile with
                | Error fault -> return Replies.operatorFault fault
                | Ok replacement ->
                    use replacement = replacement
                    let! installation = administration.RotateMasterKey(replacement, token)
                    return Replies.masterKeyRotated installation
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
                let inputs =
                    Inputs.connection ()
                    |> Result.bind (fun connection ->
                        Inputs.masterKey () |> Result.map (fun masterKey -> connection, masterKey))

                match inputs with
                | Error fault -> return Replies.operatorFault fault
                | Ok(connection, masterKey) ->
                    use masterKey = masterKey

                    try
                        use administration =
                            new Administration(connection, BuildIdentity.current.Version, masterKey)

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
