namespace ClaimCore.Postgres

open ClaimCore.Contract

open System
open System.Threading
open ClaimCore.Application
open Npgsql

/// The admitted runtime owns storage lifetime without exposing provider types.
type IRuntime =
    inherit IDisposable
    abstract Sessions: ISessionStore
    abstract Installation: Installation
    abstract Ready: CancellationToken -> System.Threading.Tasks.Task<bool>

type private RuntimeResource(source: NpgsqlDataSource, installation: Installation) =

    member _.Store =
        LedgerStore(source, installation.InstallationId, Transactions.timeouts) :> ILedgerStore

    interface IRuntime with
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

        member _.Dispose() = source.Dispose()

module Runtime =
    let internal store (runtime: IRuntime) = (runtime :?> RuntimeResource).Store

    let openConnection (connectionString: string) cancellationToken =
        task {
            let mutable owned: NpgsqlDataSource option = None

            try
                let source = NpgsqlDataSource.Create connectionString
                owned <- Some source
                let! installation = Admission.openRuntime source cancellationToken
                return Ok(new RuntimeResource(source, installation) :> IRuntime)
            with error ->
                owned |> Option.iter _.Dispose()

                return
                    Error(
                        match error with
                        | AdmissionRefusal fault -> fault
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

/// Owner provisioning retains its administration-process outcome contract.
[<RequireQualifiedAccess>]
type ProvisionOwnerOutcome =
    | Accepted of OperationReceipt
    | AlreadyAccepted of OperationReceipt
    | Rejected of Refusal
    | Failed of CoreFault
    | Cancelled
    | OutcomeUnknown of operationId: Guid

module internal LedgerAdministration =
    let verify source expected cancellationToken =
        Verification.verify source expected cancellationToken

    let head source cancellationToken =
        DatabaseSession.read source cancellationToken (fun connection ->
            task {
                ServerSupport.require connection.PostgreSqlVersion
                let! _ = Baseline.require connection cancellationToken

                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT sequence,entry_hash FROM claimcore.ledger_head"
                        [||]
                        cancellationToken

                match rows with
                | [ row ] ->
                    return
                        LedgerEntries.position
                            (DatabaseSession.value 0 row)
                            (DatabaseSession.value 1 row)
                | _ -> return invalidOp "Ledger head is absent."
            })

    let private provisionResult operationId result =
        match result with
        | Ok(Decision.Proceed receipt) -> ProvisionOwnerOutcome.Accepted receipt
        | Ok(Decision.Replay receipt) -> ProvisionOwnerOutcome.AlreadyAccepted receipt
        | Ok(Decision.Refuse reason) -> ProvisionOwnerOutcome.Rejected reason
        | Ok(Decision.Fail fault) -> ProvisionOwnerOutcome.Failed fault
        | Error StoreFailure.Cancelled -> ProvisionOwnerOutcome.Cancelled
        | Error StoreFailure.CommitOutcomeUnknown ->
            ProvisionOwnerOutcome.OutcomeUnknown operationId
        | Error StoreFailure.SchemaMismatch -> ProvisionOwnerOutcome.Failed CoreFault.SchemaMismatch
        | Error StoreFailure.Internal -> ProvisionOwnerOutcome.Failed CoreFault.InternalError
        | Error StoreFailure.Integrity -> ProvisionOwnerOutcome.Failed CoreFault.StoreIntegrityError
        | Error StoreFailure.Unavailable -> ProvisionOwnerOutcome.Failed CoreFault.StoreUnavailable

    let provisionOwner source operationId principal cancellationToken =
        task {
            let! installationId, _ =
                DatabaseSession.read source cancellationToken (fun connection ->
                    task {
                        ServerSupport.require connection.PostgreSqlVersion

                        return! Baseline.require connection cancellationToken
                    })

            let store =
                LedgerStore(source, installationId, Transactions.timeouts) :> ILedgerStore

            let! result =
                store.WriteAuthority(
                    principal,
                    operationId,
                    principal,
                    None,
                    AuthorityDecisions.provision installationId operationId principal,
                    cancellationToken
                )

            return provisionResult operationId result
        }

[<RequireQualifiedAccess>]
type AdministrationFailure =
    | Refused
    | Failed
    | OutcomeUnknown

module AdministrationFailures =
    let classify (error: exn) =
        match error with
        | :? AdministrationOutcomeUnknownException -> AdministrationFailure.OutcomeUnknown
        | AdmissionRefusal _
        | :? ArgumentException
        | :? InvalidOperationException -> AdministrationFailure.Refused
        | :? PostgresException as postgres when
            postgres.SqlState.StartsWith("42", StringComparison.Ordinal)
            ->
            AdministrationFailure.Refused
        | _ -> AdministrationFailure.Failed

/// The owner administration process supplies its connection; runtime composition does not expose it.
[<Sealed>]
type Administration(connectionString: string) =
    let source =
        Admission.transport (NpgsqlConnectionStringBuilder connectionString)
        NpgsqlDataSource.Create connectionString

    member _.Initialize(timeZone, cancellationToken) =
        SchemaAdministration.initialize source timeZone cancellationToken

    member _.Verify(expected, cancellationToken) =
        LedgerAdministration.verify source expected cancellationToken

    member _.Head(cancellationToken) =
        LedgerAdministration.head source cancellationToken

    member _.ProvisionOwner(operationId, principal, cancellationToken) =
        LedgerAdministration.provisionOwner source operationId principal cancellationToken

    interface IDisposable with
        member _.Dispose() = source.Dispose()
