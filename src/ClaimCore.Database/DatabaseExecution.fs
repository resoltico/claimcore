namespace ClaimCore.Database

open System.Threading
open System
open System.IO
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

module DatabaseExecution =
    let private primaryIdentity ownerConnection =
        let builder = OwnerConnection.builder ownerConnection
        use connection = new NpgsqlConnection(builder.ConnectionString)
        connection.Open()
        OwnerConnection.requireIdentity connection
        SchemaBaseline.requireCurrent connection

        use command =
            new NpgsqlCommand(
                "SELECT installation_id,lineage_id,witness_epoch,data_use_scope "
                + "FROM claimcore.installation_lineage WHERE singleton",
                connection
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Primary installation identity is unavailable."

        let identity: Identity =
            {
                InstallationId = reader.GetGuid(0)
                LineageId = reader.GetGuid(1)
                Epoch = reader.GetInt64(2)
            }

        let scope = InstallationUse.parseScope (reader.GetString(3))

        if reader.Read() then
            invalidOp "Primary installation identity is ambiguous."

        identity, scope

    let private requireSeparateWitness ownerConnection witnessConnection expectedUser =
        let primary = OwnerConnection.builder ownerConnection
        let witness = OwnerConnection.builder witnessConnection

        if
            witness.Username <> expectedUser
            || (String.Equals(primary.Host, witness.Host, StringComparison.OrdinalIgnoreCase)
                && primary.Port = witness.Port)
        then
            invalidOp "Independent witness administration input is invalid."

    let private initializeWitness ownerConnection witnessOwner (custody: IKeyCustody) =
        requireSeparateWitness ownerConnection witnessOwner "claimcore_witness_owner"
        let identity, scope = primaryIdentity ownerConnection
        let check = KeyCheck.create custody identity.InstallationId identity.LineageId

        use capability =
            DatabaseWitnessInputs.writerCapability ()
            |> Result.defaultWith (fun _ -> invalidOp "Private writer capability is unavailable.")

        try
            try
                capability.Use(fun material ->
                    Baseline.initialize
                        witnessOwner
                        identity
                        scope
                        custody.ActiveKeyId
                        check
                        material)

                AdministrationOutcome.Completed None
            with _ ->
                AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed
        finally
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(check)

    let private provisionInitialOwner
        ownerConnection
        witnessWriter
        (custody: IKeyCustody)
        principal
        =
        requireSeparateWitness ownerConnection witnessWriter "claimcore_witness_writer"
        let identity, _ = primaryIdentity ownerConnection

        use capability =
            DatabaseWitnessInputs.writerCapability ()
            |> Result.defaultWith (fun _ -> invalidOp "Private writer capability is unavailable.")

        let store =
            capability.Use(fun material -> new Store(witnessWriter, identity, material))

        use witness = new WitnessProtocol(store, custody, identity)
        witness.Admit(CancellationToken.None).GetAwaiter().GetResult()
        let builder = OwnerConnection.builder ownerConnection
        use connection = new NpgsqlConnection(builder.ConnectionString)
        connection.Open()
        OwnerConnection.requireIdentity connection

        ActorGrantAdministration.provisionInitialOwner connection witness principal
        |> fun work -> work.GetAwaiter().GetResult()
        |> function
            | AuthorityWriteOutcome.Applied _ -> AdministrationOutcome.Completed None
            | AuthorityWriteOutcome.Refused ->
                AdministrationOutcome.NotCommitted AdministrationFailure.OperationFailed
            | AuthorityWriteOutcome.Unconfirmed _ ->
                AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed

    let private witnessed command connection =
        let input =
            match command with
            | DatabaseCommand.InitializeWitness ->
                DatabaseWitnessInputs.witnessOwnerConnection ()
                |> Result.bind (fun witnessOwner ->
                    DatabaseWitnessInputs.keyRing ()
                    |> Result.map (fun custody -> witnessOwner, custody, None))
            | DatabaseCommand.ProvisionInitialOwner ->
                DatabaseWitnessInputs.witnessWriterConnection ()
                |> Result.bind (fun witnessWriter ->
                    DatabaseWitnessInputs.keyRing ()
                    |> Result.bind (fun custody ->
                        match DatabaseWitnessInputs.initialOwner () with
                        | Ok principal -> Ok(witnessWriter, custody, Some principal)
                        | Error reason ->
                            custody.Dispose()
                            Error reason))
            | _ -> invalidArg (nameof command) "A witnessed administration command is required."

        input
        |> Result.map (fun (witnessConnection, custody, principal) ->
            use custody = custody

            try
                match command, principal with
                | DatabaseCommand.InitializeWitness, None ->
                    let _, scope = primaryIdentity connection

                    if DatabaseInstallationScope.reviewedForScope scope then
                        initializeWitness connection witnessConnection custody
                    else
                        AdministrationOutcome.NotStarted
                            AdministrationFailure.DatabaseConfigurationInvalid
                | DatabaseCommand.ProvisionInitialOwner, Some value ->
                    provisionInitialOwner connection witnessConnection custody value
                | _ -> invalidOp "Witnessed administration input is incomplete."
            with _ ->
                AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed)

    let private ownerConnection () =
        match
            Environment.GetEnvironmentVariable("CLAIMCORE_ADMIN_CONNECTION_FILE")
            |> Option.ofObj
        with
        | None -> Error DatabaseInputProblem.ConnectionSettingMissing
        | Some path ->
            match PrivateFileService.readUtf8Text 8192 path with
            | Error _ -> Error DatabaseInputProblem.ConnectionFileRefused
            | Ok value when String.IsNullOrWhiteSpace value ->
                Error DatabaseInputProblem.ConnectionFileEmpty
            | Ok value ->
                try
                    Ok(PostgresTransport.connectionString (value.Trim()))
                with _ ->
                    Error DatabaseInputProblem.ConnectionFileRefused

    let private execute connection =
        function
        | DatabaseCommand.Verify ->
            SchemaBaseline.verify connection |> AdministrationOutcome.map (fun () -> None)
        | DatabaseCommand.Prune options ->
            PreparationPruning.prune connection options |> AdministrationOutcome.map Some
        | _ -> AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed

    let private write (stream: Stream) (bytes: byte array) =
        stream.Write(bytes, 0, bytes.Length)
        stream.Flush()

    let private bestEffort stream encode =
        try
            write stream (encode ())
        with _ ->
            ()

    /// The operation has already returned. Neither encoding, writing nor flushing can relabel it.
    let deliver command result (output: Stream) (errors: Stream) =
        let code = DatabaseDiagnostics.exitCode result
        let target = if code = 0 then output else errors

        try
            write target (DatabaseDiagnostics.outcome command result)
            code
        with _ ->
            if code = 0 then
                bestEffort errors (fun () -> DatabaseDiagnostics.deliveryFailure command result)

            if code = 4 then 4 else 3

    let inputFailure reason errors code =
        bestEffort errors (fun () -> DatabaseDiagnostics.inputFailure reason)
        code

    let private adoptedCopyCommand =
        function
        | DatabaseCommand.TransitionAdoptedCopy _
        | DatabaseCommand.VerifyDeleteAdoptedCopy _ -> true
        | _ -> false

    let private copyCommand command =
        adoptedCopyCommand command
        || (match command with
            | DatabaseCommand.RegisterCopySigner _
            | DatabaseCommand.RetireCopySigner _
            | DatabaseCommand.IngestManagedCopy _
            | DatabaseCommand.TransitionManagedCopy _
            | DatabaseCommand.VerifyDeleteManagedCopy _
            | DatabaseCommand.VerifyManagedCopy _
            | DatabaseCommand.AdoptManagedCopy _
            | DatabaseCommand.PublishExternalCopy _
            | DatabaseCommand.InspectManagedCopy _
            | DatabaseCommand.ReconcileLifecycleEvent _ -> true
            | _ -> false)

    let private initialize connection command zone realData output errors =
        let keyPath =
            Environment.GetEnvironmentVariable("CLAIMCORE_SUPPRESSION_KEY_FILE")
            |> Option.ofObj

        match keyPath with
        | None -> inputFailure DatabaseInputProblem.SuppressionKeyFileRefused errors 3
        | Some path ->
            try
                use key = SuppressionKeyFile.Load(path)

                let check installationId lineageId =
                    key.KeyId, key.Check(installationId, lineageId)

                let result =
                    (if realData then
                         SchemaBaseline.initializeRealData connection zone check
                     else
                         SchemaBaseline.initialize connection zone check)
                    |> AdministrationOutcome.map (fun () -> None)

                deliver command result output errors
            with _ ->
                inputFailure DatabaseInputProblem.SuppressionKeyFileRefused errors 3

    let private routeOwnerPrivate connection command output errors =
        DatabaseOwnerPrivateExecution.route
            connection
            command
            output
            errors
            (fun result -> deliver command result output errors)
            (fun reason -> inputFailure reason errors 3)

    let private routeSpecialized connection command output errors =
        match command with
        | DatabaseCommand.InitializeWitness
        | DatabaseCommand.ProvisionInitialOwner ->
            Some(
                match witnessed command connection with
                | Error reason -> inputFailure reason errors 3
                | Ok result -> deliver command result output errors
            )
        | DatabaseCommand.VerifyData ->
            DatabaseVerifyData.run connection
            |> fun result -> Some(DatabaseAuditDiagnostics.deliver result output errors)
        | _ -> routeOwnerPrivate connection command output errors

    let private executeOwner connection command output errors =
        match routeSpecialized connection command output errors with
        | Some code -> code
        | None ->
            match command with
            | candidate when copyCommand candidate ->
                match DatabaseCopyExecution.run command connection with
                | Error reason -> inputFailure reason errors 3
                | Ok result -> deliver command result output errors
            | DatabaseCommand.Initialize zone ->
                initialize connection command zone false output errors
            | DatabaseCommand.InitializeRealData zone ->
                initialize connection command zone true output errors
            | _ -> execute connection command |> fun result -> deliver command result output errors

    let run command output errors =
        match ownerConnection () with
        | Error reason -> inputFailure reason errors 3
        | Ok connection -> executeOwner connection command output errors
