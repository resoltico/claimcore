namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open System
open System.Threading
open ClaimCore.Application
open Npgsql

/// The admitted runtime owns storage lifetime without exposing provider types.
type IRuntime =
    inherit IDisposable
    abstract Housekeep: CancellationToken -> System.Threading.Tasks.Task<HousekeepingReport>
    abstract Sessions: ISessionStore
    abstract Installation: Installation
    abstract Ready: CancellationToken -> System.Threading.Tasks.Task<bool>

type private RuntimeResource(source: NpgsqlDataSource, installation: Installation) =

    member _.Store =
        LedgerStore(source, installation.InstallationId, Transactions.timeouts) :> IStore

    interface IRuntime with
        member _.Housekeep token =
            Housekeeping.prune source Housekeeping.DefaultBatch token

        member _.Sessions = SessionStore(source) :> ISessionStore
        member _.Installation = installation

        member _.Ready token =
            task {
                try
                    return!
                        DatabaseSession.read source token (fun connection ->
                            task {
                                let! _ = Baseline.require connection token
                                return true
                            })
                with _ ->
                    return false
            }

        member _.Dispose() =
            try
                source.Dispose()
            with error ->
                FailureDiagnostics.reportAt FailurePhase.Cleanup error
                reraise ()

module Runtime =
    /// Hosts compare their bounded concurrent storage users with the configured provider pool.
    let supportsCapacity required (connectionString: string) =
        let settings = NpgsqlConnectionStringBuilder connectionString
        settings.Pooling && settings.MaxPoolSize >= required

    let store (runtime: IRuntime) = (runtime :?> RuntimeResource).Store

    let openConnection (connectionString: string) cancellationToken =
        task {
            let mutable owned: NpgsqlDataSource option = None

            try
                let source = NpgsqlDataSource.Create connectionString
                owned <- Some source
                let! installation = InstallationCheck.openRuntime source cancellationToken
                return Ok(new RuntimeResource(source, installation) :> IRuntime)
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
                            | OperatorFault.UnsupportedServer -> RuntimeOpenFault.UnsupportedServer
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

type InitialOwner =
    {
        Principal: PrincipalKey
        Profile: ActorProfileInput
        Roles: Role list
    }

module internal LedgerAdministration =
    let verify source expected cancellationToken =
        Verification.verify source expected cancellationToken

    let head source cancellationToken =
        DatabaseSession.read source cancellationToken (fun connection ->
            task {
                ServerSupport.require connection.PostgreSqlVersion
                let! installation, _ = Baseline.require connection cancellationToken
                return! CheckpointEvidence.read connection installation cancellationToken
            })

    let initialize source productVersion timeZone (owner: InitialOwner) token =
        if not (PrincipalKey.isHuman owner.Principal) then
            raise (OperatorFaultException OperatorFault.InitialOwnerInvalid)

        match ActorProfile.parse owner.Profile, Authority.validateInitialOwnerRoles owner.Roles with
        | Ok _, Ok _ -> ()
        | _ -> raise (OperatorFaultException OperatorFault.InitialOwnerInvalid)

        let timeZone = Baseline.zone timeZone

        SchemaAdministration.initialize
            source
            timeZone
            (fun connection installationId ->
                task {
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    let! head, observed = Transactions.head connection token

                    let store =
                        LedgerStore(source, installationId, Transactions.timeouts) :> IStore

                    let zone = TimeZoneInfo.FindSystemTimeZoneById timeZone

                    let calendar =
                        { new IBusinessCalendar with
                            member _.TimeZoneId = timeZone

                            member _.DateAt instant =
                                DateOnly.FromDateTime(
                                    TimeZoneInfo.ConvertTime(instant, zone).DateTime
                                )
                        }

                    let services =
                        Service.create productVersion installationId store calendar owner.Principal

                    let tx =
                        LedgerStore.InTransaction(
                            connection,
                            installationId,
                            head,
                            observed,
                            token
                        )

                    let! result =
                        Service.provisionOwnerInTransaction
                            services
                            tx
                            (Guid.CreateVersion7(observed))
                            owner.Profile
                            owner.Roles

                    match result with
                    | Verdict.Proceed _ -> return ()
                    | Verdict.Refuse _ ->
                        return invalidOp "The initial owner could not be recorded."
                    | Verdict.Fail _
                    | Verdict.Replay _ ->
                        return raise (RecordCorruptException "Initial owner recording failed.")
                })
            token

[<RequireQualifiedAccess>]
type AdministrationFailure =
    | Fault of OperatorFault
    | OutcomeUnknown

module AdministrationFailures =
    let private installation =
        function
        | RuntimeOpenFault.InvalidTransport -> OperatorFault.UnsafeTransport
        | RuntimeOpenFault.UnsupportedServer -> OperatorFault.UnsupportedServer
        | RuntimeOpenFault.UnsafeRole -> OperatorFault.PermissionDenied
        | RuntimeOpenFault.SchemaMismatch -> OperatorFault.SchemaMismatch
        | RuntimeOpenFault.InstallationInvalid -> OperatorFault.InstallationInvalid
        | RuntimeOpenFault.HeadMismatch -> OperatorFault.HeadMismatch
        | RuntimeOpenFault.Unavailable -> OperatorFault.Unavailable
        | RuntimeOpenFault.Cancelled -> OperatorFault.Cancelled

    let classify (error: exn) =
        match error with
        | :? AdministrationOutcomeUnknownException -> AdministrationFailure.OutcomeUnknown
        | :? OperatorFaultException as error -> AdministrationFailure.Fault error.Fault
        | InstallationRefusal fault -> AdministrationFailure.Fault(installation fault)
        | :? MaintenanceRequiredException ->
            AdministrationFailure.Fault OperatorFault.MaintenanceRequired
        | :? InstallationConfigurationException ->
            AdministrationFailure.Fault OperatorFault.InstallationInvalid
        | :? RecordCorruptException -> AdministrationFailure.Fault OperatorFault.IntegrityFailure
        | :? OperationCanceledException -> AdministrationFailure.Fault OperatorFault.Cancelled
        | :? PostgresException as error ->
            let fault =
                match error.SqlState with
                | "42501" -> OperatorFault.PermissionDenied
                | code when code.StartsWith("28", StringComparison.Ordinal) ->
                    OperatorFault.ConnectionRejected
                | "42P01"
                | "42703"
                | "42883"
                | "3F000" -> OperatorFault.SchemaMismatch
                | "57P01"
                | "57P02"
                | "57P03" -> OperatorFault.Unavailable
                | code when code.StartsWith("08", StringComparison.Ordinal) ->
                    OperatorFault.Unavailable
                | "CC001"
                | "CC002"
                | "CC003"
                | "CC004"
                | "CC005" -> OperatorFault.IntegrityFailure
                | _ -> OperatorFault.InternalFailure

            AdministrationFailure.Fault fault
        | :? NpgsqlException
        | :? TimeoutException
        | :? IO.IOException -> AdministrationFailure.Fault OperatorFault.Unavailable
        | _ -> AdministrationFailure.Fault OperatorFault.InternalFailure

/// The owner administration process supplies its connection; runtime composition does not expose it.
[<Sealed>]
type Administration(connectionString: string, productVersion: string) =
    let source =
        let settings =
            try
                NpgsqlConnectionStringBuilder connectionString
            with :? ArgumentException ->
                raise (OperatorFaultException OperatorFault.ConnectionInvalid)

        if String.IsNullOrWhiteSpace settings.Host then
            raise (OperatorFaultException OperatorFault.ConnectionInvalid)

        InstallationCheck.transport settings

        try
            NpgsqlDataSource.Create connectionString
        with :? ArgumentException ->
            raise (OperatorFaultException OperatorFault.ConnectionInvalid)

    member _.Initialize(timeZone, owner, cancellationToken) =
        LedgerAdministration.initialize source productVersion timeZone owner cancellationToken

    member _.Verify(expected, cancellationToken) =
        LedgerAdministration.verify source expected cancellationToken

    member _.Rebuild(expected, cancellationToken) =
        ProjectionRebuild.rebuild source expected cancellationToken

    /// Call only with all application/bearer/CLI traffic externally isolated and maintenance active.
    member _.AdmitRestoredInstallation(checkpoint, cancellationToken) =
        RestoreAdmission.admit source checkpoint cancellationToken

    member _.Status(cancellationToken) =
        InstallationStatus.read source cancellationToken

    member _.Maintenance(change, cancellationToken) =
        RegisterMaintenanceStore.change source change cancellationToken

    member _.Head(cancellationToken) =
        LedgerAdministration.head source cancellationToken

    interface IDisposable with
        member _.Dispose() =
            FailureDiagnostics.cleanupSync source.Dispose
