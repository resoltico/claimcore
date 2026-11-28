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
            | Initialize(zone, owner, ifMissing) ->
                match Inputs.witnessConnection () with
                | Error fault -> return Replies.operatorFault fault
                | Ok witness ->
                    match witness with
                    | Some connection -> do! administration.CheckWitness(connection, token)
                    | None -> ()

                    let! result =
                        if ifMissing then
                            administration.InitializeIfMissing(zone, owner, token)
                        else
                            task {
                                let! installation = administration.Initialize(zone, owner, token)
                                return InitializationResult.Initialized installation
                            }

                    let installation, reply =
                        match result with
                        | InitializationResult.Initialized value -> value, Replies.initialized value
                        | InitializationResult.ExistingInstallation value ->
                            value, Replies.initializationSkipped value

                    match witness with
                    | Some connection ->
                        do! administration.EnrollWitness(connection, installation, token)
                    | None -> ()

                    return reply
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
            | RotateEncryptionGeneration handle ->
                let! generation = administration.RotateEncryptionGeneration(handle, token)
                return Replies.encryptionGenerationRotated generation
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
            | RepairAttachment((checkpoint, handle), caseId, blob, file) ->
                match ClaimCore.HostSecurity.PrivateFile.read AttachmentLimits.FileBytes file with
                | Error _ -> return Replies.operatorFault OperatorFault.AttachmentFileRefused
                | Ok bytes ->
                    try
                        let! result =
                            administration.RepairAttachment(
                                checkpoint,
                                handle,
                                caseId,
                                blob,
                                ReadOnlyMemory bytes,
                                token
                            )

                        return Replies.attachmentRepair result
                    finally
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(Span bytes)
            | AdmitRestoredInstallation(checkpoint, handle) ->
                match Inputs.restoreWitness () with
                | Error fault -> return Replies.operatorFault fault
                | Ok(connection, transition, history) ->
                    let! result =
                        administration.AdmitWitnessedRestore(
                            checkpoint,
                            handle,
                            connection,
                            transition,
                            history,
                            token
                        )

                    return Replies.restoreAdmission result
            | Status ->
                let! status = administration.Status(token)
                return Replies.status status
            | Maintenance(MaintenanceChange.Resume handle as change) ->
                match Inputs.witnessConnection () with
                | Error fault -> return Replies.operatorFault fault
                | Ok connection ->
                    let! result =
                        match connection with
                        | Some witness -> administration.ResumeWithWitness(handle, witness, token)
                        | None -> administration.Maintenance(change, token)

                    return Replies.maintenance result
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
