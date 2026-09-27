namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// The owner-only command accepts only an opaque private file path. The proposal's claimant
/// reference and reason are never command-line arguments, diagnostics or output fields.
module internal DatabasePurgeExecution =
    let private separate ownerConnection witnessConnection =
        let primary = OwnerConnection.builder ownerConnection
        let witness = OwnerConnection.builder witnessConnection

        witness.Username = "claimcore_witness_writer"
        && not (
            String.Equals(primary.Host, witness.Host, StringComparison.OrdinalIgnoreCase)
            && primary.Port = witness.Port
        )

    let private mapOutcome =
        function
        | OwnerPurgeOutcome.Purged _ -> AdministrationOutcome.Completed None
        | OwnerPurgeOutcome.Unconfirmed _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed
        | OwnerPurgeOutcome.Refused _
        | OwnerPurgeOutcome.InventoryUnknown
        | OwnerPurgeOutcome.IdentityCoverageUnknowable
        | OwnerPurgeOutcome.AuditUnavailable _ ->
            AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed

    let private witnessed
        ownerConnection
        witnessConnection
        (custody: IKeyCustody)
        (suppression: SuppressionKeyFile)
        (proposal: byte array)
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
            capability.Use(fun material -> new Store(witnessConnection, identity, material))

        use witness = new WitnessProtocol(store, custody, identity)

        match DatabaseManagedCopyInventory.TryLoadFromPrivateConfiguration() with
        | None -> AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
        | Some inventory ->
            use inventory = inventory

            CaseErasurePurge.execute
                ownerConnection
                owner
                witness
                commitments
                (inventory :> IManagedCopyErasureClearance)
                proposal
                CancellationToken.None
            |> fun work -> work.GetAwaiter().GetResult()
            |> mapOutcome

    let private withKeys ownerConnection proposal =
        match DatabaseWitnessInputs.witnessWriterConnection () with
        | Error reason -> Error reason
        | Ok writer when not (separate ownerConnection writer) ->
            Error DatabaseInputProblem.WitnessFileInvalid
        | Ok writer ->
            match DatabaseWitnessInputs.keyRing () with
            | Error reason -> Error reason
            | Ok custody ->
                use custody = custody

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
                            witnessed ownerConnection writer custody suppression proposal
                        with _ ->
                            AdministrationOutcome.CompletionUnknown
                                AdministrationFailure.CommitUnconfirmed)

    let run ownerConnection proposalPath =
        match PrivateFileService.readBinary 16384 proposalPath with
        | Error _ -> Error DatabaseInputProblem.ErasureProposalFileRefused
        | Ok proposal when proposal.Length = 0 ->
            CryptographicOperations.ZeroMemory(proposal)
            Error DatabaseInputProblem.ErasureProposalFileRefused
        | Ok proposal ->
            try
                withKeys ownerConnection proposal
            finally
                CryptographicOperations.ZeroMemory(proposal)
