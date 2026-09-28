namespace ClaimCore.Database

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// Separate owner process. Its credential and private files never enter Web or CLI case work.
module internal DatabaseCopyExecution =
    let private separate ownerConnection witnessConnection =
        let primary = OwnerConnection.builder ownerConnection
        let witness = OwnerConnection.builder witnessConnection

        witness.Username = "claimcore_witness_writer"
        && not (
            String.Equals(primary.Host, witness.Host, StringComparison.OrdinalIgnoreCase)
            && primary.Port = witness.Port
        )

    let private primaryIdentity ownerConnection =
        let builder = OwnerConnection.builder ownerConnection
        use connection = new NpgsqlConnection(builder.ConnectionString)
        connection.Open()
        OwnerConnection.requireIdentity connection
        SchemaBaseline.requireCurrent connection

        use command =
            new NpgsqlCommand(
                "SELECT installation_id,lineage_id,witness_epoch "
                + "FROM claimcore.installation_lineage WHERE singleton",
                connection
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Primary installation identity is unavailable."

        let result: Identity =
            {
                InstallationId = reader.GetGuid(0)
                LineageId = reader.GetGuid(1)
                Epoch = reader.GetInt64(2)
            }

        if reader.Read() then
            invalidOp "Primary installation identity is ambiguous."

        result

    let private outcome =
        function
        | AuthorityWriteOutcome.Applied _ -> AdministrationOutcome.Completed None
        | AuthorityWriteOutcome.Refused ->
            AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
        | AuthorityWriteOutcome.Unconfirmed _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed

    let private register
        connection
        witness
        purpose
        eventId
        keyId
        path
        ownerApproval
        custodianApproval
        =
        match DatabaseCopyInputs.publicKey path with
        | Error problem -> Error problem
        | Ok publicKey ->
            try
                ManagedCopySignerAdministration.register
                    connection
                    witness
                    eventId
                    keyId
                    purpose
                    publicKey
                    ownerApproval
                    custodianApproval
                |> fun work -> work.GetAwaiter().GetResult()
                |> outcome
                |> Ok
            finally
                CryptographicOperations.ZeroMemory(publicKey)

    let private ingest connection witness attestationPath signaturePath =
        match
            DatabaseCopyInputs.attestation attestationPath,
            DatabaseCopyInputs.signature signaturePath
        with
        | Ok canonical, Ok signature ->
            try
                ManagedCopyAdministration.ingest connection witness canonical signature
                |> fun work -> work.GetAwaiter().GetResult()
                |> outcome
                |> Ok
            finally
                CryptographicOperations.ZeroMemory(canonical)
                CryptographicOperations.ZeroMemory(signature)
        | _ -> Error DatabaseInputProblem.ManagedCopyFileRefused

    let private transition connection witness attestationPath signaturePath =
        match
            DatabaseCopyInputs.attestation attestationPath,
            DatabaseCopyInputs.signature signaturePath
        with
        | Ok canonical, Ok signature ->
            try
                ManagedCopyTransitionAdministration.transition
                    connection
                    witness
                    canonical
                    signature
                |> fun work -> work.GetAwaiter().GetResult()
                |> outcome
                |> Ok
            finally
                CryptographicOperations.ZeroMemory(canonical)
                CryptographicOperations.ZeroMemory(signature)
        | _ -> Error DatabaseInputProblem.ManagedCopyFileRefused

    let private verifyDelete connection witness attestationPath signaturePath =
        match
            DatabaseCopyInputs.attestation attestationPath,
            DatabaseCopyInputs.signature signaturePath
        with
        | Ok canonical, Ok signature ->
            try
                match DatabaseManagedCopyInventory.TryLoadFromPrivateConfiguration() with
                | None ->
                    AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed |> Ok
                | Some inventory ->
                    use inventory = inventory

                    ManagedCopyVerifiedDeletionExecution.execute
                        connection
                        witness
                        (inventory :> ICopyAbsenceVerifier)
                        canonical
                        signature
                    |> fun work -> work.GetAwaiter().GetResult()
                    |> outcome
                    |> Ok
            finally
                CryptographicOperations.ZeroMemory(canonical)
                CryptographicOperations.ZeroMemory(signature)
        | _ -> Error DatabaseInputProblem.ManagedCopyFileRefused

    let private verifyManaged connection witness attestationPath signaturePath =
        match DatabaseCopyInputs.attestation attestationPath with
        | Error _ -> Error DatabaseInputProblem.ManagedCopyFileRefused
        | Ok canonical ->
            try
                match DatabaseCopyInputs.signature signaturePath with
                | Error _ -> Error DatabaseInputProblem.ManagedCopyFileRefused
                | Ok signature ->
                    try
                        match DatabaseManagedCopyPhysicalInputs.load () with
                        | Error reason -> Error reason
                        | Ok physical ->
                            try
                                use verifier = new DatabaseManagedCopyPhysicalVerifier(physical)

                                ManagedCopyVerifiedRestore.execute
                                    connection
                                    witness
                                    (verifier :> IManagedCopyPhysicalVerifier)
                                    canonical
                                    signature
                                |> fun work -> work.GetAwaiter().GetResult()
                                |> outcome
                                |> Ok
                            finally
                                CryptographicOperations.ZeroMemory(physical.Proof)
                                CryptographicOperations.ZeroMemory(physical.Signature)
                    finally
                        CryptographicOperations.ZeroMemory(signature)
            finally
                CryptographicOperations.ZeroMemory(canonical)

    let private reconcile connection witness eventId =
        let result =
            CaseLifecycleReconcile.reconcile
                connection
                witness
                eventId
                Threading.CancellationToken.None
            |> fun work -> work.GetAwaiter().GetResult()

        match result with
        | LifecycleReconcileOutcome.Settled _ -> Ok(AdministrationOutcome.Completed None)
        | LifecycleReconcileOutcome.PrimaryAbsentUnknown _
        | LifecycleReconcileOutcome.IntegrityMismatch _
        | LifecycleReconcileOutcome.Unconfirmed _ ->
            Ok(AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed)

    let private inspect connection witness copyId =
        let verified =
            ManagedCopyOwnerInspection.inspect connection witness copyId
            |> fun work -> work.GetAwaiter().GetResult()

        if verified then
            Ok(AdministrationOutcome.Completed None)
        else
            Ok(AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed)

    let private executeOrdinary command (connection: NpgsqlConnection) witness =
        match command with
        | DatabaseCommand.RegisterCopySigner(purpose,
                                             eventId,
                                             keyId,
                                             path,
                                             ownerApproval,
                                             custodianApproval) ->
            register connection witness purpose eventId keyId path ownerApproval custodianApproval
        | DatabaseCommand.RetireCopySigner(purpose, eventId, keyId, ownerApproval, custodianApproval) ->
            ManagedCopySignerAdministration.retire
                connection
                witness
                eventId
                keyId
                purpose
                ownerApproval
                custodianApproval
            |> fun work -> work.GetAwaiter().GetResult()
            |> outcome
            |> Ok
        | DatabaseCommand.IngestManagedCopy(attestationPath, signaturePath) ->
            ingest connection witness attestationPath signaturePath
        | DatabaseCommand.TransitionManagedCopy(attestationPath, signaturePath) ->
            transition connection witness attestationPath signaturePath
        | DatabaseCommand.VerifyDeleteManagedCopy(attestationPath, signaturePath) ->
            verifyDelete connection witness attestationPath signaturePath
        | DatabaseCommand.VerifyManagedCopy(attestationPath, signaturePath) ->
            verifyManaged connection witness attestationPath signaturePath
        | DatabaseCommand.ReconcileLifecycleEvent eventId -> reconcile connection witness eventId
        | DatabaseCommand.InspectManagedCopy copyId -> inspect connection witness copyId
        | _ -> Error DatabaseInputProblem.UnsupportedInvocation

    let private execute ownerConnection command (connection: NpgsqlConnection) witness =
        match DatabaseCopyCustodyExecution.route ownerConnection command connection witness with
        | Some result -> result
        | None -> executeOrdinary command connection witness

    let run command ownerConnection =
        DatabaseWitnessInputs.witnessWriterConnection ()
        |> Result.bind (fun witnessConnection ->
            DatabaseWitnessInputs.keyRing ()
            |> Result.map (fun custody -> witnessConnection, custody))
        |> Result.map (fun (witnessConnection, custody) ->
            use custody = custody
            let mutable actionEntered = false

            try
                if not (separate ownerConnection witnessConnection) then
                    AdministrationOutcome.NotStarted AdministrationFailure.OwnerConnectionInvalid
                    |> Ok
                else
                    let identity = primaryIdentity ownerConnection

                    use capability =
                        DatabaseWitnessInputs.writerCapability ()
                        |> Result.defaultWith (fun _ ->
                            invalidOp "Private writer capability is unavailable.")

                    let store =
                        capability.Use(fun material ->
                            new Store(witnessConnection, identity, material))

                    use witness = new WitnessProtocol(store, custody, identity)
                    witness.Admit()
                    let builder = OwnerConnection.builder ownerConnection
                    use connection = new NpgsqlConnection(builder.ConnectionString)
                    connection.Open()
                    OwnerConnection.requireIdentity connection
                    actionEntered <- true
                    execute ownerConnection command connection witness
            with _ ->
                if actionEntered then
                    Ok(
                        AdministrationOutcome.CompletionUnknown
                            AdministrationFailure.CommitUnconfirmed
                    )
                else
                    Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed))
        |> Result.bind id
