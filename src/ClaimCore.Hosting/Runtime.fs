namespace ClaimCore.Hosting

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres

module private RuntimeOpening =
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

    let core (dataSource: NpgsqlDataSource) (cancellationToken: CancellationToken) =
        task {
            // The opening connection verifies server, role, ACL, and exact schema with this token.
            use! _connection =
                RuntimeDatabase.openConnectionAsyncWithCancellation dataSource cancellationToken

            let! businessTimeZone =
                InstallationBusinessZone.requireConfigured _connection cancellationToken

            cancellationToken.ThrowIfCancellationRequested()
            let store = new PostgresStore(dataSource)
            let recovery = new PostgresRecoveryStore(dataSource, PreparationLimits.defaults)

            match! (recovery :> IRecoveryStore).InstallationLineage cancellationToken with
            | Error failure -> return Error(recoveryFault failure)
            | Ok _ ->
                cancellationToken.ThrowIfCancellationRequested()

                let clock = businessTime businessTimeZone

                return Ok(CoreApi.create (store :> IClaimStore) (recovery :> IRecoveryStore) clock)
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

/// Trusted local composition root. No store, mutable accepted state or credential is exposed.
[<Sealed>]
type Runtime private (inner: IClaimsCore, dataSource: NpgsqlDataSource) =
    let admission = new RuntimeAdmission(dataSource, TimeSpan.FromSeconds 30.)
    let core = RuntimeCoreFacade.wrap admission inner

    member _.Core = core

    static member OpenPostgres(connectionString: string, cancellationToken: CancellationToken) =
        RuntimeSourceOwnership.openOwned
            (fun () ->
                BuildIdentity.requireCompatibleAssembly typeof<Runtime>.Assembly
                BuildIdentity.requireCompatibleAssembly typeof<PostgresStore>.Assembly
                RuntimeDataSource.create connectionString)
            (fun dataSource -> RuntimeOpening.core dataSource cancellationToken)
            (fun inner dataSource -> new Runtime(inner, dataSource))
            RuntimeOpening.fault
            cancellationToken

    interface IDisposable with
        member _.Dispose() = (admission :> IDisposable).Dispose()
