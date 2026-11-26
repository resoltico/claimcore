namespace ClaimCore.Application.Administration

open System
open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Execution
open ClaimCore.Application.Storage
open ClaimCore.Application.Verification

[<RequireQualifiedAccess>]
type InitializationResult =
    | Initialized of Installation
    | ExistingInstallation of Installation

module internal LedgerAdministration =
    let verify source masterKey expected cancellationToken =
        Verification.verify source masterKey expected cancellationToken

    let head source masterKey cancellationToken =
        DatabaseSession.read source cancellationToken (fun connection ->
            task {
                do! OwnerAdmission.require connection cancellationToken
                let! installation = Baseline.require connection cancellationToken

                use! _cipher =
                    Baseline.loadCipher
                        connection
                        installation.InstallationId
                        masterKey
                        cancellationToken

                return!
                    CheckpointEvidence.read
                        connection
                        installation.InstallationId
                        cancellationToken
            })

    let initialize
        source
        masterKey
        productVersion
        timeZone
        requestedPolicy
        (owner: InitialOwner)
        initialized
        existing
        token
        =
        if not (PrincipalKey.isHuman owner.Principal) then
            raise (OperatorFaultException OperatorFault.InitialOwnerInvalid)

        match
            ActorProfile.parse owner.Profile,
            ClaimCore.Domain.Tasks.Registry.validateInitialOwnerRoles owner.Roles
        with
        | Ok _, Ok _ -> ()
        | _ -> raise (OperatorFaultException OperatorFault.InitialOwnerInvalid)

        let timeZone = Baseline.zone timeZone

        Baseline.initializeWith
            source
            masterKey
            timeZone
            requestedPolicy
            (fun connection (installation: Installation) cipher ->
                task {
                    let! _ = Transactions.configure connection Transactions.timeouts token
                    let! head, observed = Transactions.head connection token

                    let store =
                        LedgerStore(source, installation, cipher, Transactions.timeouts) :> IStore

                    let calendar = InstallationCheck.calendar timeZone

                    let services =
                        Service.create
                            productVersion
                            installation.InstallationId
                            store
                            calendar
                            owner.Principal

                    let tx =
                        LedgerStore.InTransaction(
                            connection,
                            installation,
                            cipher,
                            head,
                            observed,
                            token
                        )

                    let! result =
                        Service.provisionOwnerInTransaction
                            services
                            tx
                            (Transactions.operationId observed)
                            owner.Profile
                            owner.Roles
                            installation.CaseReferencePolicy

                    match result with
                    | Verdict.Proceed _ -> return ()
                    | Verdict.Refuse _ ->
                        return invalidOp "The initial owner could not be recorded."
                    | Verdict.Fail _
                    | Verdict.Replay _ ->
                        return raise (RecordCorruptException "Initial owner recording failed.")
                })
            initialized
            existing
            token

    let recoverOwner
        (source: NpgsqlDataSource)
        masterKey
        productVersion
        (input: OwnerRecoveryInput)
        token
        =
        let refuse fault = raise (OperatorFaultException fault)

        DatabaseSession.transaction
            source
            Data.IsolationLevel.ReadCommitted
            token
            (fun connection transaction ->
                task {
                    do! OwnerAdmission.require connection token
                    let! installation = Baseline.require connection token

                    use! cipher =
                        Baseline.loadCipher connection installation.InstallationId masterKey token

                    let! _ = Transactions.configure connection Transactions.timeouts token
                    let! head, observed = Transactions.head connection token

                    let tx =
                        LedgerStore.InTransaction(
                            connection,
                            installation,
                            cipher,
                            head,
                            observed,
                            token
                        )

                    let! actorId =
                        match input.ActorId with
                        | Some _ -> System.Threading.Tasks.Task.FromResult input.ActorId
                        | None -> PersonAcquisition.principalOwner tx input.Principal

                    let! target =
                        match actorId with
                        | Some id -> PersonAcquisition.person tx id
                        | None -> System.Threading.Tasks.Task.FromResult None

                    let binding =
                        match actorId, target with
                        | Some id, Some person -> Binding.People(id, person.Revision)
                        | None, None -> Binding.PeopleNew Revision.zero
                        | _ -> refuse OperatorFault.OwnerRecoveryRefused

                    let store =
                        LedgerStore(source, installation, cipher, Transactions.timeouts) :> IStore

                    let services =
                        Service.create
                            productVersion
                            installation.InstallationId
                            store
                            (InstallationCheck.calendar installation.BusinessTimeZone)
                            input.Principal

                    let operation =
                        Transactions.operationId observed |> OperationId.create |> Option.get

                    let! result =
                        OperationService.recoverOwnerInTransaction
                            services
                            tx
                            operation
                            binding
                            input.Name

                    match result with
                    | Verdict.Proceed _ ->
                        let owner =
                            actorId |> Option.defaultValue (ActorId.ofOperationId operation)

                        do! DatabaseSession.commit transaction token
                        return OwnerRecoveryResult.Recovered(owner, operation)
                    | Verdict.Refuse(Refusal.People PeopleRefusal.NoChanges) when actorId.IsSome ->
                        return OwnerRecoveryResult.Unchanged actorId.Value
                    | Verdict.Refuse(Refusal.People PeopleRefusal.IdentityReserved) ->
                        return refuse OperatorFault.OwnerRecoveryIdentityReserved
                    | Verdict.Refuse(Refusal.InvalidInput _) when
                        input.Name.IsNone && target.IsNone
                        ->
                        return refuse OperatorFault.OwnerRecoveryNameRequired
                    | Verdict.Refuse _ -> return refuse OperatorFault.OwnerRecoveryRefused
                    | Verdict.Fail _
                    | Verdict.Replay _ ->
                        return raise (RecordCorruptException "Owner recovery recording failed.")
                })

module internal AdministrationFailures =
    let private installation =
        function
        | RuntimeOpenFault.InvalidTransport -> OperatorFault.UnsafeTransport
        | RuntimeOpenFault.UnsupportedServer -> OperatorFault.UnsupportedServer
        | RuntimeOpenFault.UnsupportedDatabase -> OperatorFault.UnsupportedDatabase
        | RuntimeOpenFault.UnsafeRole -> OperatorFault.PermissionDenied
        | RuntimeOpenFault.SchemaMismatch -> OperatorFault.SchemaMismatch
        | RuntimeOpenFault.InstallationInvalid -> OperatorFault.InstallationInvalid
        | RuntimeOpenFault.DataKeyInvalid -> OperatorFault.DataKeyInvalid
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
type Administration(connectionString: string, productVersion: string, masterKey: MasterKey) =
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
            InstallationCheck.dataSource connectionString
        with :? ArgumentException ->
            raise (OperatorFaultException OperatorFault.ConnectionInvalid)

    static member ClassifyFailure error = AdministrationFailures.classify error

    member _.Initialize(timeZone, owner, requestedPolicy, cancellationToken) =
        LedgerAdministration.initialize
            source
            masterKey
            productVersion
            timeZone
            requestedPolicy
            owner
            id
            Baseline.refuseExisting
            cancellationToken

    member _.InitializeIfMissing(timeZone, owner, requestedPolicy, cancellationToken) =
        LedgerAdministration.initialize
            source
            masterKey
            productVersion
            timeZone
            requestedPolicy
            owner
            InitializationResult.Initialized
            (fun connection installation token ->
                task {
                    do! Baseline.requireBootstrap installation connection token

                    if Option.exists ((<>) installation.CaseReferencePolicy) requestedPolicy then
                        raise (OperatorFaultException OperatorFault.CaseReferencePolicyMismatch)

                    return InitializationResult.ExistingInstallation installation
                })
            cancellationToken

    member _.RecoverOwner(input, cancellationToken) =
        LedgerAdministration.recoverOwner source masterKey productVersion input cancellationToken

    member _.Verify(expected, cancellationToken) =
        LedgerAdministration.verify source masterKey expected cancellationToken

    member _.Rebuild(expected, handle, cancellationToken) =
        ProjectionRebuild.rebuild source masterKey expected handle cancellationToken

    /// Call only with all application/bearer/CLI traffic externally isolated and maintenance active.
    member _.AdmitRestoredInstallation(checkpoint, handle, cancellationToken) =
        RestoreAdmission.admit source masterKey checkpoint handle cancellationToken

    member _.Status(cancellationToken) =
        InstallationStatus.read source masterKey cancellationToken

    member _.Maintenance(change, cancellationToken) =
        RegisterMaintenanceStore.change source masterKey change cancellationToken

    member _.Head(cancellationToken) =
        LedgerAdministration.head source masterKey cancellationToken

    member _.RotateMasterKey(newMasterKey, cancellationToken) =
        Baseline.rotate source masterKey newMasterKey cancellationToken

    interface IDisposable with
        member _.Dispose() =
            FailureDiagnostics.cleanupSync source.Dispose
