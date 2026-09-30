namespace ClaimCore.Hosting

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

module internal RuntimeOpening =
    let private writerCapabilityPath () =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Private writer capability is unavailable.")

    let private businessTime zoneId =
        let zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId)

        { new IBusinessTime with
            member _.Capture() =
                let observed = TimeProvider.System.GetUtcNow()
                let local = TimeZoneInfo.ConvertTime(observed, zone)

                {
                    ObservedUtcInstant = observed
                    EffectiveBusinessDate = DateOnly.FromDateTime(local.DateTime)
                    TimeZoneId = zoneId
                }
        }

    let private recoveryFault value =
        match value with
        | RecoveryStoreFailure.SchemaMismatch -> RuntimeOpenFault.RuntimeSchemaMismatch
        | RecoveryStoreFailure.StoreCorrupt -> RuntimeOpenFault.RuntimeStoreIntegrityError
        | RecoveryStoreFailure.ReadCancelled -> RuntimeOpenFault.RuntimeCancelled
        | _ -> RuntimeOpenFault.RuntimeStoreUnavailable

    let private installation (connection: NpgsqlConnection) (cancellationToken: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT installation_id,lineage_id,witness_epoch,suppression_key_id,"
                    + "suppression_key_check FROM claimcore.installation_lineage",
                    connection
                )

            use! reader = command.ExecuteReaderAsync(cancellationToken)

            if not (reader.Read()) then
                invalidOp "Primary installation identity is missing."

            let identity =
                {
                    InstallationId = reader.GetGuid(0)
                    LineageId = reader.GetGuid(1)
                    Epoch = reader.GetInt64(2)
                }

            let keyId = reader.GetGuid(3)
            let check = reader.GetFieldValue<byte array>(4)
            return identity, keyId, check
        }

    let private requireBootstrapNoCases (useState: InstallationUseState) (audit: DataAuditSummary) =
        if
            useState.Scope = InstallationUseScope.RealData
            && useState.Phase = InstallationUsePhase.BootstrapNoCases
            && (audit.Cases <> 0L
                || audit.AcceptedOperations <> 0L
                || audit.LifecycleEvents <> 0L
                || audit.ErasureFences <> 0L
                || audit.ManagedExports <> 0L
                || audit.PendingIntents <> 0L)
        then
            invalidOp "Real-data bootstrap contains claimant or pending authority."

    let core
        (resources: RuntimeResources)
        (witnessConnection: string)
        (custodyFactory: Store -> IKeyCustody)
        (suppressionKeyFilePath: string)
        (cancellationToken: CancellationToken)
        =
        task {
            let dataSource = resources.DataSource
            // The opening connection verifies server, role, ACL, and exact schema with this token.
            use! _connection =
                RuntimeDatabase.openConnectionAsyncWithCancellation dataSource cancellationToken

            let! businessTimeZone =
                InstallationBusinessZone.requireConfigured _connection cancellationToken

            let! identity, suppressionKeyId, suppressionCheck =
                installation _connection cancellationToken

            let suppression =
                SuppressionKeyCustody.load
                    suppressionKeyFilePath
                    identity.InstallationId
                    identity.LineageId
                    suppressionKeyId
                    suppressionCheck

            resources.AttachSuppression suppression
            use _artifactKeys = RecoveryArtifactKeyCustody.load resources.ArtifactKeyRingPath

            use writerCapability = WriterCapabilityFile.Load(writerCapabilityPath ())

            let witnessStore =
                writerCapability.Use(fun material ->
                    new Store(witnessConnection, identity, material))

            let custody = custodyFactory witnessStore
            let witness = new WitnessProtocol(witnessStore, custody, identity)
            resources.Attach(witness)
            witness.Admit()

            cancellationToken.ThrowIfCancellationRequested()

            match! RecoveryStoreQueries.installationLineage dataSource cancellationToken with
            | Error failure -> return Error(recoveryFault failure)
            | Ok _ ->
                cancellationToken.ThrowIfCancellationRequested()

                // Admission must prove the complete current primary projection against the
                // independent witness before any actor-bound case work becomes available.
                let! audit = RuntimeFullAudit.run resources cancellationToken

                let useState = InstallationUseScopeRead.requirePair _connection witness
                requireBootstrapNoCases useState audit

                let clock = businessTime businessTimeZone
                resources.AttachClock(clock)

                return Ok()
        }

    let fault (error: exn) =
        match error with
        | :? OperationCanceledException -> RuntimeOpenFault.RuntimeCancelled
        | :? ArgumentException
        | :? InvalidOperationException -> RuntimeOpenFault.RuntimeConfigurationInvalid
        | RuntimeDatabaseMismatch
        | UnsupportedPostgresVersion -> RuntimeOpenFault.RuntimeSchemaMismatch
        | :? InvalidDataException
        | :? InvalidCastException
        | :? IndexOutOfRangeException -> RuntimeOpenFault.RuntimeStoreIntegrityError
        | _ -> RuntimeOpenFault.RuntimeStoreUnavailable
