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
        (Released<EmailAddress * Map<DigestCountKind, int64>>
            -> CancellationToken
            -> Task<DigestRelayResult>) *
        (ActorId -> DigestOutcome -> unit) *
        CancellationToken ->
            Task<DigestRunSummary>

    abstract Sessions: ISessionStore
    abstract Installation: Installation
    abstract Ready: CancellationToken -> System.Threading.Tasks.Task<bool>

type private RuntimeResource
    (
        source: NpgsqlDataSource,
        installation: Installation,
        cipher: InstallationCipher,
        witness: (RecoveryWitness * WitnessPrimary) option
    ) =

    member _.Store =
        LedgerStore(source, installation, cipher, Transactions.timeouts, ?witness = witness)
        :> IStore

    member _.RecoveryReady token =
        match witness with
        | Some(store, primary) -> store.IsActive(primary, token)
        | None -> Task.FromResult true

    interface IRuntime with
        member _.Housekeep(dateAt, token) =
            task {
                do!
                    Transactions.settleCredentials
                        source
                        Transactions.timeouts
                        (Baseline.requireExpected installation)
                        token

                return!
                    Housekeeping.prune source installation Housekeeping.DefaultBatch dateAt token
            }

        member this.RunDigests(basis, send, record, token) =
            let admit ct =
                task {
                    let! openForDispatch =
                        DatabaseSession.read source ct (fun connection ->
                            task {
                                let! maintenance = RegisterMaintenanceStore.read connection ct
                                return not maintenance.Active
                            })

                    match witness with
                    | Some(store, primary) when openForDispatch ->
                        return! store.AdmitDispatch(primary, basis.BusinessDate, ct)
                    | Some _ -> return false
                    | None -> return openForDispatch
                }

            task {
                let! admitted = admit token

                if admitted then
                    let guardedSend released ct =
                        task {
                            let! admitted = admit ct

                            if admitted then
                                return! send released ct
                            else
                                return DigestRelayResult.Refused
                        }

                    let authorizeRelease = DisclosureReads.digest this.Store

                    return!
                        DigestDispatch.run
                            source
                            installation
                            basis
                            guardedSend
                            authorizeRelease
                            record
                            token
                else
                    // Fencing dispatch cannot erase what is already known about intake by the relay.
                    do! DigestStorage.reconcile source installation token
                    return! DigestStorage.summary source installation token
            }

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

                                return!
                                    Baseline.currentEncryptionGeneration
                                        connection
                                        cipher.Generation
                                        token
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
                try
                    (cipher :> IDisposable).Dispose()
                finally
                    witness |> Option.iter (fun (store, _) -> (store :> IDisposable).Dispose())

module Runtime =
    let correlate correlation =
        StorageDiagnostics.correlate correlation

    let subscribeFailures observer = StorageDiagnostics.subscribe observer

    /// Hosts compare their bounded concurrent storage users with the configured provider pool.
    let supportsCapacity required (connectionString: string) =
        let settings = NpgsqlConnectionStringBuilder connectionString
        settings.Pooling && settings.MaxPoolSize >= required

    let internal store (runtime: IRuntime) = (runtime :?> RuntimeResource).Store

    let recoveryReady (runtime: IRuntime) token =
        (runtime :?> RuntimeResource).RecoveryReady token

    let service (runtime: IRuntime) productVersion calendar principal =
        Service.create
            productVersion
            runtime.Installation.InstallationId
            (store runtime)
            calendar
            principal

    let private openConfigured
        (connectionString: string)
        (witnessConnection: string option)
        masterKey
        cancellationToken
        =
        task {
            let mutable owned: NpgsqlDataSource option = None
            let mutable ownedWitness: RecoveryWitness option = None

            try
                let source = InstallationCheck.dataSource connectionString
                owned <- Some source

                let! installation, cipher =
                    InstallationCheck.openRuntime source masterKey cancellationToken

                try
                    let! witness =
                        task {
                            match witnessConnection with
                            | None -> return None
                            | Some connection ->
                                let settings = NpgsqlConnectionStringBuilder connection
                                InstallationCheck.transport settings

                                settings.CancellationTimeout <- 500

                                let! primary =
                                    DatabaseSession.read source cancellationToken (fun conn ->
                                        WitnessPrimary.read conn cancellationToken)

                                let target = InstallationCheck.dataSource settings.ConnectionString

                                let witness =
                                    new RecoveryWitness(target, installation.InstallationId)

                                ownedWitness <- Some witness

                                do!
                                    DatabaseSession.read target cancellationToken (fun connection ->
                                        WitnessCluster.requireIndependent
                                            primary.SystemId
                                            connection
                                            cancellationToken)

                                return Some(witness, primary)
                        }

                    do!
                        Transactions.settleCredentials
                            source
                            Transactions.timeouts
                            (Baseline.requireExpected installation)
                            cancellationToken

                    return
                        Ok(new RuntimeResource(source, installation, cipher, witness) :> IRuntime)
                with error ->
                    (cipher :> IDisposable).Dispose()
                    return raise error
            with error ->
                match ownedWitness with
                | Some witness ->
                    do!
                        FailureDiagnostics.cleanup (fun () ->
                            (witness :> IDisposable).Dispose()
                            ValueTask())
                | None -> ()

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
                            | OperatorFault.WitnessMismatch -> RuntimeOpenFault.WitnessMismatch
                            | OperatorFault.DataKeyInvalid -> RuntimeOpenFault.DataKeyInvalid
                            | OperatorFault.UnsupportedServer -> RuntimeOpenFault.UnsupportedServer
                            | OperatorFault.UnsupportedDatabase ->
                                RuntimeOpenFault.UnsupportedDatabase
                            | _ -> RuntimeOpenFault.InstallationInvalid
                        | :? InstallationConfigurationException
                        | :? RecordCorruptException -> RuntimeOpenFault.InstallationInvalid
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

    let openConnection connection masterKey token =
        openConfigured connection None masterKey token

    let openWitnessedConnection connection witness masterKey token =
        openConfigured connection (Some witness) masterKey token
