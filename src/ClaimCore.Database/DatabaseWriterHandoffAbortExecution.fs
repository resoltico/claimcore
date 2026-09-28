namespace ClaimCore.Database

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// Separate owner process: A1/A2/A3 use two signed owner files, never a runtime endpoint.
module internal DatabaseWriterHandoffAbortExecution =
    let private privateAppConnection () =
        match Environment.GetEnvironmentVariable("CLAIMCORE_CONNECTION_FILE") with
        | null
        | "" -> Error DatabaseInputProblem.ConnectionSettingMissing
        | path ->
            match PrivateFileService.readUtf8Text 8192 path with
            | Ok value when not (String.IsNullOrWhiteSpace value) -> Ok(value.Trim())
            | _ -> Error DatabaseInputProblem.ConnectionFileRefused

    let private separate (owner: string) (app: string) (audit: string) (witnessOwner: string) =
        let primary = OwnerConnection.builder owner
        let runtime = NpgsqlConnectionStringBuilder(app)
        let witness = NpgsqlConnectionStringBuilder(audit)
        let administrative = NpgsqlConnectionStringBuilder(witnessOwner)

        runtime.Username = "claimcore_app"
        && runtime.Host = primary.Host
        && runtime.Port = primary.Port
        && runtime.Database = primary.Database
        && witness.Username = "claimcore_witness_auditor"
        && administrative.Username = "claimcore_witness_owner"
        && witness.Host = administrative.Host
        && witness.Port = administrative.Port
        && witness.Database = administrative.Database
        && not (
            String.Equals(primary.Host, witness.Host, StringComparison.OrdinalIgnoreCase)
            && primary.Port = witness.Port
        )

    let sourceInput owner =
        privateAppConnection ()
        |> Result.bind (fun app ->
            DatabaseWitnessInputs.witnessAuditConnection ()
            |> Result.bind (fun audit ->
                DatabaseWitnessInputs.witnessOwnerConnection ()
                |> Result.bind (fun witnessOwner ->
                    if separate owner app audit witnessOwner then
                        Ok(app, audit, witnessOwner)
                    else
                        Error DatabaseInputProblem.WitnessFileInvalid)))

    let private outcome =
        function
        | WriterHandoffAbortOutcome.Released _ -> AdministrationOutcome.Completed None
        | WriterHandoffAbortOutcome.AwaitingPrimary _
        | WriterHandoffAbortOutcome.AwaitingRelease _
        | WriterHandoffAbortOutcome.Unconfirmed _
        | WriterHandoffAbortOutcome.Refused ->
            AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed

    let private runStages
        (owner: NpgsqlConnection)
        source
        witnessOwner
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        canonical
        signatureOne
        signatureTwo
        (capability: WriterCapabilityFile)
        =
        capability.Use(fun oldCapability ->
            let stage =
                WriterHandoffOwnerAbortStage.start
                    owner
                    source
                    witnessOwner
                    witness
                    (Some commitments)
                    canonical
                    signatureOne
                    signatureTwo
                    oldCapability
                |> fun task -> task.GetAwaiter().GetResult()

            match stage with
            | WriterHandoffAbortOutcome.AwaitingPrimary _ ->
                let backfill =
                    WriterHandoffOwnerAbortBackfill.commit
                        owner
                        witness
                        canonical
                        signatureOne
                        signatureTwo
                    |> fun task -> task.GetAwaiter().GetResult()

                match backfill with
                | WriterHandoffAbortOutcome.AwaitingRelease _ ->
                    WriterHandoffOwnerAbortRelease.release
                        owner
                        source
                        witnessOwner
                        witness
                        (Some commitments)
                        canonical
                        signatureOne
                        signatureTwo
                        oldCapability
                    |> fun task -> task.GetAwaiter().GetResult()
                    |> outcome
                | other -> outcome other
            | other -> outcome other)

    let private execute
        ownerConnection
        app
        audit
        witnessOwner
        (custody: IKeyCustody)
        (suppression: SuppressionKeyFile)
        canonical
        signatureOne
        signatureTwo
        =
        let builder = OwnerConnection.builder ownerConnection
        use owner = new NpgsqlConnection(builder.ConnectionString)
        owner.Open()
        OwnerConnection.requireIdentity owner
        SchemaBaseline.requireCurrent owner
        let identity, keyId, check = DatabaseVerifyData.identity owner
        let commitments = DatabaseVerifyData.commitments suppression identity keyId check
        use source = RuntimeDataSource.create app
        let store = Store.OpenAudit(audit, identity)
        use witness = new WitnessProtocol(store, custody, identity)
        witness.AdmitReadOnly()

        use capability =
            DatabaseWitnessInputs.writerCapability ()
            |> Result.defaultWith (fun _ -> invalidOp "Private writer capability is unavailable.")

        runStages
            owner
            source
            witnessOwner
            witness
            commitments
            canonical
            signatureOne
            signatureTwo
            capability

    let private withPrivateConfiguration owner canonical signatureOne signatureTwo =
        sourceInput owner
        |> Result.bind (fun (app, audit, witnessOwner) ->
            DatabaseWitnessInputs.keyRing ()
            |> Result.bind (fun custody ->
                use custody = custody

                match Environment.GetEnvironmentVariable("CLAIMCORE_SUPPRESSION_KEY_FILE") with
                | null
                | "" -> Error DatabaseInputProblem.SuppressionKeyFileRefused
                | path ->
                    try
                        use suppression = SuppressionKeyFile.Load(path)

                        execute
                            owner
                            app
                            audit
                            witnessOwner
                            custody
                            suppression
                            canonical
                            signatureOne
                            signatureTwo
                        |> Ok
                    with _ ->
                        Ok(
                            AdministrationOutcome.CompletionUnknown
                                AdministrationFailure.CommitUnconfirmed
                        )))

    let run owner candidatePath signatureOnePath signatureTwoPath =
        match
            DatabaseCopyInputs.attestation candidatePath,
            DatabaseCopyInputs.signature signatureOnePath,
            DatabaseCopyInputs.signature signatureTwoPath
        with
        | Ok canonical, Ok signatureOne, Ok signatureTwo ->
            try
                if WriterHandoffAbort.parse canonical |> Option.isNone then
                    Error DatabaseInputProblem.WriterHandoffFileRefused
                else
                    withPrivateConfiguration owner canonical signatureOne signatureTwo
            finally
                CryptographicOperations.ZeroMemory(canonical)
                CryptographicOperations.ZeroMemory(signatureOne)
                CryptographicOperations.ZeroMemory(signatureTwo)
        | _ -> Error DatabaseInputProblem.WriterHandoffFileRefused
