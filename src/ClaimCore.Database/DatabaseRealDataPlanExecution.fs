namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Postgres
open ClaimCore.Witness

/// Owner-only publication from independently signed private source to actor-reviewable,
/// witnessed nonclaimant plan evidence; no actor can create its own plan.
module internal DatabaseRealDataPlanExecution =
    let private identity (owner: NpgsqlConnection) =
        use command =
            new NpgsqlCommand(
                "SELECT installation_id,lineage_id,witness_epoch "
                + "FROM claimcore.installation_lineage WHERE singleton",
                owner
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Owner installation identity is absent."

        let value: Identity =
            {
                InstallationId = reader.GetGuid(0)
                LineageId = reader.GetGuid(1)
                Epoch = reader.GetInt64(2)
            }

        if reader.Read() then
            invalidOp "Owner installation identity is ambiguous."

        value

    let private separate ownerConnection writerConnection =
        let owner = OwnerConnection.builder ownerConnection
        let writer = OwnerConnection.builder writerConnection

        writer.Username = "claimcore_witness_writer"
        && not (
            String.Equals(owner.Host, writer.Host, StringComparison.OrdinalIgnoreCase)
            && owner.Port = writer.Port
        )

    let private publish ownerConnection writer custody plan output =
        if not (separate ownerConnection writer) then
            AdministrationOutcome.NotStarted AdministrationFailure.DatabaseConfigurationInvalid
        else
            let builder = OwnerConnection.builder ownerConnection
            use owner = new NpgsqlConnection(builder.ConnectionString)
            owner.Open()
            OwnerConnection.requireIdentity owner
            SchemaBaseline.requireCurrent owner
            let installed = identity owner

            use capability =
                DatabaseWitnessInputs.writerCapability ()
                |> Result.defaultWith (fun _ -> invalidOp "Private witness capability is absent.")

            let store = capability.Use(fun material -> new Store(writer, installed, material))
            use witness = new WitnessProtocol(store, custody, installed)

            let result =
                InstallationUsePlanPublisher.publish owner witness plan CancellationToken.None
                |> fun task -> task.GetAwaiter().GetResult()

            match result with
            | InstallationUsePlanOutcome.Refused ->
                AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
            | InstallationUsePlanOutcome.Unconfirmed _ ->
                AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed
            | InstallationUsePlanOutcome.Published(planId, activationId) ->
                if DatabaseRealDataPlanReceipt.publish output planId activationId plan then
                    AdministrationOutcome.Completed None
                else
                    AdministrationOutcome.CompletedCleanupFailed None

    let run ownerConnection policyPath originalEvidencePath outputPath =
        match
            DatabaseBackupHealthExecution.activationPlan
                ownerConnection
                policyPath
                originalEvidencePath
            |> fun work -> work.GetAwaiter().GetResult()
        with
        | Error reason -> Error reason
        | Ok None -> Ok(AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed)
        | Ok(Some plan) ->
            try
                match DatabaseWitnessInputs.witnessWriterConnection () with
                | Error reason -> Error reason
                | Ok writer ->
                    match DatabaseWitnessInputs.keyRing () with
                    | Error reason -> Error reason
                    | Ok custody ->
                        use custody = custody

                        try
                            Ok(publish ownerConnection writer custody plan outputPath)
                        with _ ->
                            Ok(
                                AdministrationOutcome.CompletionUnknown
                                    AdministrationFailure.CommitUnconfirmed
                            )
            finally
                CryptographicOperations.ZeroMemory(plan.Canonical)
