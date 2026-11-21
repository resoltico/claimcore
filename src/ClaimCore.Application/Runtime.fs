namespace ClaimCore.Application

open ClaimCore.Application.Verification

open ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open Npgsql

/// The admitted runtime owns storage lifetime without exposing provider types.
type IRuntime =
    inherit IDisposable
    abstract Housekeep: (DateTimeOffset -> DateOnly) * CancellationToken -> Task<HousekeepingReport>

    abstract RunDigests:
        WorkDateBasis *
        (EmailAddress -> Map<DigestCountKind, int64> -> CancellationToken -> Task<bool>) *
        (ActorId -> DigestOutcome -> unit) *
        CancellationToken ->
            Task<unit>

    abstract Sessions: ISessionStore
    abstract Installation: Installation
    abstract Ready: CancellationToken -> System.Threading.Tasks.Task<bool>

type private RuntimeResource
    (source: NpgsqlDataSource, installation: Installation, cipher: InstallationCipher) =

    member _.Store =
        LedgerStore(source, installation, cipher, Transactions.timeouts) :> IStore

    interface IRuntime with
        member _.Housekeep(dateAt, token) =
            Housekeeping.prune source installation Housekeeping.DefaultBatch dateAt token

        member _.RunDigests(basis, send, record, token) =
            DigestDispatch.run source installation basis send record token

        member _.Sessions = SessionStore(source, installation) :> ISessionStore
        member _.Installation = installation

        member _.Ready token =
            task {
                try
                    return!
                        DatabaseSession.read source token (fun connection ->
                            task {
                                let! _ =
                                    Transactions.configure connection Transactions.timeouts token

                                do! Baseline.requireExpected installation connection token
                                return true
                            })
                with _ ->
                    return false
            }

        member _.Dispose() =
            try
                try
                    source.Dispose()
                with error ->
                    FailureDiagnostics.reportAt FailurePhase.Cleanup error
                    reraise ()
            finally
                (cipher :> IDisposable).Dispose()

module Runtime =
    let correlate correlation =
        StorageDiagnostics.correlate correlation

    let subscribeFailures observer = StorageDiagnostics.subscribe observer

    /// Hosts compare their bounded concurrent storage users with the configured provider pool.
    let supportsCapacity required (connectionString: string) =
        let settings = NpgsqlConnectionStringBuilder connectionString
        settings.Pooling && settings.MaxPoolSize >= required

    let internal store (runtime: IRuntime) = (runtime :?> RuntimeResource).Store

    let service (runtime: IRuntime) productVersion calendar principal =
        Service.create
            productVersion
            runtime.Installation.InstallationId
            (store runtime)
            calendar
            principal

    let openConnection (connectionString: string) masterKey cancellationToken =
        task {
            let mutable owned: NpgsqlDataSource option = None

            try
                let source = InstallationCheck.dataSource connectionString
                owned <- Some source

                let! installation, cipher =
                    InstallationCheck.openRuntime source masterKey cancellationToken

                return Ok(new RuntimeResource(source, installation, cipher) :> IRuntime)
            with error ->
                match owned with
                | Some source -> do! FailureDiagnostics.cleanup source.DisposeAsync
                | None -> ()

                return
                    Error(
                        match error with
                        | InstallationRefusal fault -> fault
                        | :? OperatorFaultException as fault ->
                            match fault.Fault with
                            | OperatorFault.SchemaMismatch
                            | OperatorFault.FormatMismatch -> RuntimeOpenFault.SchemaMismatch
                            | OperatorFault.DataKeyInvalid -> RuntimeOpenFault.DataKeyInvalid
                            | OperatorFault.UnsupportedServer -> RuntimeOpenFault.UnsupportedServer
                            | OperatorFault.UnsupportedDatabase ->
                                RuntimeOpenFault.UnsupportedDatabase
                            | _ -> RuntimeOpenFault.InstallationInvalid
                        | :? InstallationConfigurationException ->
                            RuntimeOpenFault.InstallationInvalid
                        | :? OperationCanceledException -> RuntimeOpenFault.Cancelled
                        | :? PostgresException as error when
                            error.SqlState.StartsWith("42", StringComparison.Ordinal)
                            ->
                            RuntimeOpenFault.SchemaMismatch
                        | :? InvalidOperationException -> RuntimeOpenFault.SchemaMismatch
                        | :? ArgumentException -> RuntimeOpenFault.InvalidTransport
                        | _ -> RuntimeOpenFault.Unavailable
                    )
        }
