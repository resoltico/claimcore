namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// Owner-only terminal copy-absence and suppression-horizon commands.
module internal DatabaseTerminalCopyAbsenceExecution =
    let private separate ownerConnection witnessWriter =
        let owner = OwnerConnection.builder ownerConnection
        let writer = OwnerConnection.builder witnessWriter

        writer.Username = "claimcore_witness_writer"
        && not (
            String.Equals(owner.Host, writer.Host, StringComparison.OrdinalIgnoreCase)
            && owner.Port = writer.Port
        )

    let private outcome =
        function
        | OwnerTerminalOutcome.Advanced _ -> AdministrationOutcome.Completed None
        | OwnerTerminalOutcome.Unconfirmed _ ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed
        | OwnerTerminalOutcome.Refused _
        | OwnerTerminalOutcome.ResourceUnavailable
        | OwnerTerminalOutcome.InventoryUnknown
        | OwnerTerminalOutcome.RecoveryFenceUnknown
        | OwnerTerminalOutcome.AuditUnavailable _ ->
            AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed

    let private execute
        ownerConnection
        writer
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
            |> Result.defaultWith (fun _ -> invalidOp "Private witness capability is unavailable.")

        let store = capability.Use(fun material -> new Store(writer, identity, material))
        use witness = new WitnessProtocol(store, custody, identity)

        match DatabaseTerminalCopyAbsenceIssuer.TryLoadFromPrivateConfiguration() with
        | None -> AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
        | Some issuer ->
            use issuer = issuer

            use recoveryFence =
                DatabaseTerminalRecoveryFenceIssuer.Load(ownerConnection, proposal)

            try
                CaseTombstoneTerminalOwner.execute
                    ownerConnection
                    witness
                    commitments
                    (issuer :> ICopyErasureCertification)
                    (recoveryFence :> IRecoveryFenceCertification)
                    proposal
                    CancellationToken.None
                |> fun work -> work.GetAwaiter().GetResult()
                |> outcome
            with _ ->
                AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed

    let private withKeys ownerConnection proposal =
        DatabaseWitnessInputs.witnessWriterConnection ()
        |> Result.bind (fun writer ->
            if not (separate ownerConnection writer) then
                Error DatabaseInputProblem.WitnessFileInvalid
            else
                DatabaseWitnessInputs.keyRing ()
                |> Result.bind (fun custody ->
                    use custody = custody

                    match
                        Environment.GetEnvironmentVariable("CLAIMCORE_SUPPRESSION_KEY_FILE")
                    with
                    | null
                    | "" -> Error DatabaseInputProblem.SuppressionKeyFileRefused
                    | path ->
                        let admitted =
                            try
                                Ok(SuppressionKeyFile.Load(path))
                            with _ ->
                                Error DatabaseInputProblem.SuppressionKeyFileRefused

                        admitted
                        |> Result.map (fun suppression ->
                            use suppression = suppression

                            try
                                execute ownerConnection writer custody suppression proposal
                            with _ ->
                                AdministrationOutcome.NotCommitted
                                    AdministrationFailure.OperationFailed)))

    let private runExpected final ownerConnection proposalPath =
        match PrivateFileService.readBinary 8192 proposalPath with
        | Error _ -> Error DatabaseInputProblem.ErasureProposalFileRefused
        | Ok bytes ->
            try
                match CaseTombstoneTerminalProposalCodec.decode bytes with
                | Some(TombstoneTerminalProposal.ConfirmManagedPayloadAbsence _ as proposal) when
                    not final
                    ->
                    withKeys ownerConnection proposal
                | Some(TombstoneTerminalProposal.CompleteSuppressionHorizon _ as proposal) when
                    final
                    ->
                    withKeys ownerConnection proposal
                | _ -> Error DatabaseInputProblem.ErasureProposalFileRefused
            finally
                CryptographicOperations.ZeroMemory(bytes)

    let run ownerConnection proposalPath =
        runExpected false ownerConnection proposalPath

    let runFinal ownerConnection proposalPath =
        runExpected true ownerConnection proposalPath
