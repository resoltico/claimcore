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


module private RuntimeActorFactory =
    let create (resources: RuntimeResources) context =
        let artifactAuthority =
            { new IRecoveryArtifactAuthority with
                member _.Sign(retained, ct) =
                    RecoveryArtifactAuthority.sign
                        resources.DataSource
                        resources.Witness
                        resources.Clock
                        context
                        resources.ArtifactKeyRingPath
                        retained
                        ct

                member _.Verify(source, ct) =
                    RecoveryArtifactAuthority.verify
                        resources.DataSource
                        resources.Witness
                        resources.Clock
                        context
                        resources.ArtifactKeyRingPath
                        source
                        ct
            }

        let claims =
            new PostgresStore(
                resources.DataSource,
                resources.Witness,
                context,
                resources.CursorProtection,
                resources.Clock
            )

        let recovery =
            new PostgresRecoveryStore(
                resources.DataSource,
                PreparationLimits.defaults,
                resources.Witness,
                context
            )

        CoreApi.createActor
            (claims :> IClaimStore)
            (recovery :> IRecoveryStore)
            resources.Clock
            context
            artifactAuthority


/// Trusted local composition root. No store, mutable accepted state or credential is exposed.
[<Sealed>]
type Runtime private (resources: RuntimeResources) =
    let safety = new RuntimeSafetySupervisor(resources)

    let auditCadence =
        new RuntimeAuditCadence(resources, RuntimeAuditInterval.configured ())

    let realDataScope = safety.CurrentUseState().Scope = InstallationUseScope.RealData

    let commitHealth =
        { new ICaseMutationCommitHealth with
            member _.VerifyLocked(connection, transaction) =
                if realDataScope then
                    RuntimeBackupHealthFiles.requireLocked resources connection transaction
        }

    let admission =
        new RuntimeAdmission(
            { new IDisposable with
                member _.Dispose() =
                    try
                        (auditCadence :> IDisposable).Dispose()
                    finally
                        (resources :> IDisposable).Dispose()
            },
            TimeSpan.FromSeconds 30.,
            safety.RequireCurrent,
            safety.AcquireReadFence,
            {
                RequireCaseMutation =
                    (fun () ->
                        auditCadence.RequireHealthy()
                        safety.RequireCaseMutation())
                RequireCaseRead =
                    (fun () ->
                        auditCadence.RequireHealthy()
                        safety.RequireCaseRead())
                RequireAuthoritySetup =
                    (fun () ->
                        auditCadence.RequireHealthy()
                        safety.RequireAuthoritySetup())
                RequireAuthorityRead =
                    (fun () ->
                        auditCadence.RequireHealthy()
                        safety.RequireAuthorityRead())
                CommitHealth = commitHealth
                CommitHealthRequired = realDataScope
            }
        )

    /// Safe operator-facing scope/phase observation; no case data or private evidence leaves.
    member _.DataUseReadiness() =
        try
            let state = safety.CurrentUseState()

            let ready =
                if
                    state.Scope <> InstallationUseScope.RealData
                    || state.Phase <> InstallationUsePhase.Active
                then
                    false
                else
                    try
                        auditCadence.RequireHealthy()
                        safety.RequireCaseMutation()
                        true
                    with _ ->
                        false

            InstallationUse.scopeToken state.Scope, InstallationUse.phaseToken state.Phase, ready
        with _ ->
            "UNKNOWN", "QUARANTINED", false

    /// The caller passes a PrincipalKey obtained from validated OIDC/OAuth, not a display
    /// name or group claim. A fresh scoped core is composed for each admitted endpoint call.
    member private _.ForActorUsing(principal: PrincipalKey, selectedAdmission: RuntimeAdmission) =
        use _lease = selectedAdmission.Admit()

        let gate =
            new PostgresActorGate(resources.DataSource, resources.Suppression) :> IActorGate

        let factory = RuntimeActorFactory.create resources

        let management =
            new PostgresActorManagement(resources.DataSource, resources.Witness, principal)
            :> IActorManagement

        let lifecycleStore =
            new PostgresCaseLifecycleStore(
                resources.DataSource,
                resources.Witness,
                resources.Suppression
            )
            :> ICaseLifecycleStore

        let lifecycle =
            ActorLifecycleApi.create gate lifecycleStore resources.Clock principal

        let tombstoneStore =
            new PostgresCaseTombstoneStore(resources.DataSource, resources.Witness)
            :> ITombstoneStore

        let tombstones =
            ActorTombstoneApi.create gate tombstoneStore resources.Clock principal

        let signerApproval = RuntimeActorApprovalStores.signer resources
        let deletionApproval = RuntimeActorApprovalStores.deletion resources
        let adoptionApproval = RuntimeActorApprovalStores.adoption resources
        let handoffApproval = RuntimeActorApprovalStores.handoff resources

        let realDataActivationApproval =
            RuntimeActorApprovalStores.realDataActivation resources

        ActorCoreApi.create
            gate
            factory
            principal
            management
            lifecycle
            tombstones
            signerApproval
            deletionApproval
            adoptionApproval
            handoffApproval
            realDataActivationApproval
        |> RuntimeActorFacade.wrap selectedAdmission gate principal

    member this.ForActor(principal: PrincipalKey) : IActorClaimsCore =
        this.ForActorUsing(principal, admission)

    /// Internal qualification seam; it cannot select a reviewed trust root or reach
    /// the public Web/CLI composition path.
    member internal this.ForActorWithAdmission(principal: PrincipalKey, selectedAdmission) =
        this.ForActorUsing(principal, selectedAdmission)

    static member private OpenWithCustody
        (
            connectionString: string,
            witnessConnection: string,
            custodyFactory: Store -> IKeyCustody,
            suppressionKeyFilePath: string,
            artifactKeyRingPath: string,
            cancellationToken: CancellationToken
        ) =
        RuntimeSourceOwnership.openOwned
            (fun () ->
                BuildIdentity.requireCompatibleAssembly typeof<Runtime>.Assembly
                BuildIdentity.requireCompatibleAssembly typeof<PostgresStore>.Assembly
                BuildIdentity.requireCompatibleAssembly typeof<Store>.Assembly
                new RuntimeResources(connectionString, artifactKeyRingPath))
            (fun resources ->
                RuntimeOpening.core
                    resources
                    witnessConnection
                    custodyFactory
                    suppressionKeyFilePath
                    cancellationToken)
            (fun () resources -> new Runtime(resources))
            RuntimeOpening.fault
            cancellationToken

    static member OpenPostgres
        (
            connectionString: string,
            witnessConnection: string,
            witnessKey: byte array,
            suppressionKeyFilePath: string,
            artifactKeyRingPath: string,
            cancellationToken: CancellationToken
        ) =
        Runtime.OpenWithCustody(
            connectionString,
            witnessConnection,
            (fun store ->
                let activeKeyId, _ = store.ReadKeyCheck()
                new KeyRing(activeKeyId, [ activeKeyId, witnessKey ]) :> IKeyCustody),
            suppressionKeyFilePath,
            artifactKeyRingPath,
            cancellationToken
        )

    static member OpenPostgres
        (
            connectionString: string,
            witnessConnection: string,
            witnessKeyRingFilePath: string,
            suppressionKeyFilePath: string,
            artifactKeyRingPath: string,
            cancellationToken: CancellationToken
        ) =
        Runtime.OpenWithCustody(
            connectionString,
            witnessConnection,
            (fun _ -> WitnessKeyCustody.load witnessKeyRingFilePath),
            suppressionKeyFilePath,
            artifactKeyRingPath,
            cancellationToken
        )

    interface IDisposable with
        member _.Dispose() = (admission :> IDisposable).Dispose()
