namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// Owner-only opaque private-file command. No case reference, reason, claimant bytes or key
/// material enter argv or result diagnostics; the outcome never calls erasure final.
module internal DatabasePruneWitnessExecution =
    let private separate primaryConnection witnessWriter witnessOwner =
        let primary = OwnerConnection.builder primaryConnection
        let writer = OwnerConnection.builder witnessWriter
        let owner = OwnerConnection.builder witnessOwner

        writer.Username = "claimcore_witness_writer"
        && owner.Username = "claimcore_witness_owner"
        && not (
            String.Equals(primary.Host, writer.Host, StringComparison.OrdinalIgnoreCase)
            && primary.Port = writer.Port
        )
        && String.Equals(writer.Host, owner.Host, StringComparison.OrdinalIgnoreCase)
        && writer.Port = owner.Port
        && writer.Database = owner.Database

    let private mapOutcome =
        function
        | OwnerWitnessPruneOutcome.WitnessPayloadPruned _ -> AdministrationOutcome.Completed None
        | OwnerWitnessPruneOutcome.Unconfirmed _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed
        | OwnerWitnessPruneOutcome.Refused _
        | OwnerWitnessPruneOutcome.ResourceUnavailable
        | OwnerWitnessPruneOutcome.InventoryUnknown
        | OwnerWitnessPruneOutcome.AuditUnavailable _ ->
            AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed

    let private execute
        ownerConnection
        writerConnection
        witnessOwnerConnection
        (custody: IKeyCustody)
        (suppression: SuppressionKeyFile)
        proposal
        =
        let builder = OwnerConnection.builder ownerConnection
        use owner = new NpgsqlConnection(builder.ConnectionString)
        owner.Open()
        OwnerConnection.requireIdentity owner
        SchemaBaseline.requireCurrent owner
        let identity, keyId, check = DatabaseVerifyData.identity owner
        let commitments = DatabaseVerifyData.commitments suppression identity keyId check

        use capability =
            DatabaseWitnessInputs.writerCapability ()
            |> Result.defaultWith (fun _ -> invalidOp "Private writer capability is unavailable.")

        let store =
            capability.Use(fun material -> new Store(writerConnection, identity, material))

        use witness = new WitnessProtocol(store, custody, identity)

        match DatabaseManagedCopyInventory.TryLoadFromPrivateConfiguration() with
        | None -> AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
        | Some inventory ->
            use inventory = inventory

            CaseTombstonePruneOwner.execute
                ownerConnection
                witnessOwnerConnection
                witness
                commitments
                (inventory :> IManagedCopyErasureClearance)
                proposal
                CancellationToken.None
            |> fun work -> work.GetAwaiter().GetResult()
            |> mapOutcome

    let private withSuppression owner writer witnessOwner (custody: IKeyCustody) proposal =
        match Environment.GetEnvironmentVariable("CLAIMCORE_SUPPRESSION_KEY_FILE") with
        | null
        | "" -> Error DatabaseInputProblem.SuppressionKeyFileRefused
        | path ->
            let key =
                try
                    Ok(SuppressionKeyFile.Load(path))
                with _ ->
                    Error DatabaseInputProblem.SuppressionKeyFileRefused

            key
            |> Result.map (fun suppression ->
                use suppression = suppression

                try
                    execute owner writer witnessOwner custody suppression proposal
                with _ ->
                    AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed)

    let private withWitness owner proposal =
        DatabaseWitnessInputs.witnessWriterConnection ()
        |> Result.bind (fun writer ->
            DatabaseWitnessInputs.witnessOwnerConnection ()
            |> Result.bind (fun witnessOwner ->
                if not (separate owner writer witnessOwner) then
                    Error DatabaseInputProblem.WitnessFileInvalid
                else
                    DatabaseWitnessInputs.keyRing ()
                    |> Result.bind (fun custody ->
                        use custody = custody
                        withSuppression owner writer witnessOwner custody proposal)))

    let run ownerConnection proposalPath =
        match PrivateFileService.readBinary 8192 proposalPath with
        | Error _ -> Error DatabaseInputProblem.ErasureProposalFileRefused
        | Ok bytes ->
            try
                match CaseTombstoneProposalCodec.decode bytes with
                | None -> Error DatabaseInputProblem.ErasureProposalFileRefused
                | Some proposal -> withWitness ownerConnection proposal
            finally
                CryptographicOperations.ZeroMemory(bytes)
