namespace ClaimCore.Database

open System
open System.Security.Cryptography
open ClaimCore.HostSecurity
open ClaimCore.Postgres

/// Owner-only W1 composition never exposes private proof bytes to case-work transports.
module internal DatabaseWriterHandoffExecution =
    let private stage (inputs: WriterHandoffExecutionInputs) stage =
        match stage with
        | "PREPARE" ->
            let witnessWriter =
                DatabaseWitnessInputs.witnessWriterConnection ()
                |> Result.defaultWith (fun _ ->
                    invalidOp "Witness writer connection is unavailable.")

            let proposal =
                WriterHandoffPreparation.parse inputs.Canonical
                |> Option.defaultWith (fun () -> invalidOp "W1 PREPARE is invalid.")

            DatabaseWriterHandoffStages.prepared inputs witnessWriter proposal
        | "COMMIT" ->
            let value =
                WriterHandoffSettlement.parse inputs.Canonical
                |> Option.defaultWith (fun () -> invalidOp "W1 SETTLE is invalid.")

            DatabaseWriterHandoffStages.settled inputs value
        | _ -> invalidOp "W1 stage is invalid."

    let private configured ownerConnection canonical signature stageName =
        let app, audit, witnessOwner =
            DatabaseWriterHandoffAbortExecution.sourceInput ownerConnection
            |> Result.defaultWith (fun _ -> invalidOp "Owner W1 connections are unavailable.")

        use custody =
            DatabaseWitnessInputs.keyRing ()
            |> Result.defaultWith (fun _ -> invalidOp "Private witness key ring is unavailable.")

        use suppression =
            SuppressionKeyFile.Load(
                DatabaseWriterHandoffPrivate.privatePath "CLAIMCORE_SUPPRESSION_KEY_FILE"
            )

        use oldCapability =
            DatabaseWitnessInputs.writerCapability ()
            |> Result.defaultWith (fun _ -> invalidOp "Old writer capability is unavailable.")

        use newCapability =
            WriterCapabilityFile.Load(
                DatabaseWriterHandoffPrivate.privatePath "CLAIMCORE_NEW_WRITER_CAPABILITY_FILE"
            )

        let files = DatabaseWriterHandoffPrivate.reportFiles ()
        let fenceBody, fenceSignature = DatabaseWriterHandoffPrivate.fenceFiles ()

        try
            stage
                {
                    OwnerConnection = ownerConnection
                    App = app
                    WitnessAudit = audit
                    WitnessOwner = witnessOwner
                    Custody = custody
                    Suppression = suppression
                    Files = files
                    FenceBody = fenceBody
                    FenceSignature = fenceSignature
                    Canonical = canonical
                    Signature = signature
                    OldCapability = oldCapability
                    NewCapability = newCapability
                }
                stageName
        finally
            DatabaseRestoreReportInputs.dispose files
            CryptographicOperations.ZeroMemory(fenceBody)
            CryptographicOperations.ZeroMemory(fenceSignature)

    let run ownerConnection canonicalPath signaturePath stageName =
        match
            DatabaseCopyInputs.attestation canonicalPath, DatabaseCopyInputs.signature signaturePath
        with
        | Ok canonical, Ok signature ->
            try
                if DatabaseRestorePublication.current () |> Option.isNone then
                    Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed)
                else
                    try
                        Ok(configured ownerConnection canonical signature stageName)
                    with _ ->
                        Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed)
            finally
                CryptographicOperations.ZeroMemory(canonical)
                CryptographicOperations.ZeroMemory(signature)
        | _ -> Error DatabaseInputProblem.WriterHandoffFileRefused
