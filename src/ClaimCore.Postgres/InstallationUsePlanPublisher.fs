namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Witness
open WitnessProtocolReconciliation

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal InstallationUsePlanOutcome =
    | Published of planId: Guid * activationId: Guid
    | Refused
    | Unconfirmed of planId: Guid

/// Owner-published source-reviewed plan; witnessed before any actor may review/approve it.
module internal InstallationUsePlanPublisher =
    let private identities (plan: BackupHealthActivationPlan) =
        let digest = Convert.FromHexString plan.PlanSha256

        InstallationUseActivationCandidate.planIdFromDigest plan.InstallationId digest,
        InstallationUseActivationCandidate.eventIdFromPlan plan.InstallationId digest

    let private valid (plan: BackupHealthActivationPlan) =
        match ReviewedDeploymentRoot.current () with
        | None -> false
        | Some profile ->
            try
                plan.Canonical.Length >= 2
                && plan.Canonical.Length <= 16384
                && plan.PlanSha256 = (SHA256.HashData(plan.Canonical) |> Convert.ToHexStringLower)
                && plan.PolicySha256 = profile.BackupHealthPolicySha256
                && plan.PublicationRootSha256 =
                    (SHA256.HashData(profile.PublicationRootKey) |> Convert.ToHexStringLower)
            finally
                CryptographicOperations.ZeroMemory(profile.PublicationRootKey)

    let private insert
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        planId
        activationId
        (plan: BackupHealthActivationPlan)
        (intent: WitnessIntent)
        =
        use command =
            new NpgsqlCommand(
                "INSERT INTO claimcore.installation_data_use_plans "
                + "(plan_id,activation_id,installation_id,lineage_id,witness_epoch,writer_generation,"
                + "policy_sha256,plan_sha256,canonical_plan,published_at,"
                + "witness_intent_sequence,witness_epoch_at_publication,witness_intent_hash) "
                + "VALUES (@plan,@activation,@installation,@lineage,@epoch,@generation,"
                + "@policy,@digest,@canonical,clock_timestamp(),@sequence,@witnessEpoch,@hash)",
                connection,
                transaction
            )

        Sql.uuid command "plan" planId
        Sql.uuid command "activation" activationId
        Sql.uuid command "installation" plan.InstallationId
        Sql.uuid command "lineage" plan.LineageId
        Sql.integer command "epoch" plan.Epoch
        Sql.integer command "generation" plan.WriterGeneration
        Sql.add command "policy" NpgsqlDbType.Bytea (box (Convert.FromHexString plan.PolicySha256))
        Sql.add command "digest" NpgsqlDbType.Bytea (box (Convert.FromHexString plan.PlanSha256))
        Sql.add command "canonical" NpgsqlDbType.Bytea (box plan.Canonical)
        Sql.integer command "sequence" intent.Ticket.Sequence
        Sql.integer command "witnessEpoch" intent.Ticket.Epoch
        Sql.add command "hash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)

        if command.ExecuteNonQuery() <> 1 then
            invalidOp "Reviewed plan did not persist."

    let private existing
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        planId
        (plan: BackupHealthActivationPlan)
        =
        use command =
            new NpgsqlCommand(
                "SELECT canonical_plan,witness_intent_sequence,witness_epoch_at_publication,"
                + "witness_intent_hash FROM claimcore.installation_data_use_plans "
                + "WHERE plan_id=@plan",
                connection,
                transaction
            )

        Sql.uuid command "plan" planId
        use reader = command.ExecuteReader()

        let value =
            if reader.Read() then
                Some(
                    reader.GetFieldValue<byte array>(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetFieldValue<byte array>(3)
                )
            else
                None

        if reader.Read() then
            invalidOp "Reviewed plan is duplicated."

        reader.Close()

        match value with
        | None -> false
        | Some(canonical, sequence, epoch, hash) ->
            if canonical <> plan.Canonical then
                invalidOp "Reviewed plan retry changed bytes."

            witness.ReconcileAuthority(planId, sequence, epoch, hash, canonical)
            true

    let private underLock
        (primaryOwner: NpgsqlConnection)
        (witness: WitnessProtocol)
        (plan: BackupHealthActivationPlan)
        (planId: Guid)
        (activationId: Guid)
        (started: bool ref)
        (ct: CancellationToken)
        =
        task {
            use! _authorityFence = AuthorityOperationFence.acquireShared None primaryOwner ct
            use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)
            let! _ = ActorGrantRead.lockRevision primaryOwner transaction true ct

            let identity, generation, scope, phase, _, _, _ =
                InstallationUseActivationPrimary.state primaryOwner transaction

            let snapshot = witness.Snapshot()

            if
                identity <> (plan.InstallationId, plan.LineageId, plan.Epoch)
                || generation <> plan.WriterGeneration
                || scope <> InstallationUseScope.RealData
                || phase <> InstallationUsePhase.BootstrapNoCases
                || snapshot.Use.Scope <> InstallationUseScope.RealData
                || snapshot.Use.Phase <> InstallationUsePhase.BootstrapNoCases
            then
                return InstallationUsePlanOutcome.Refused
            elif existing primaryOwner transaction witness planId plan then
                let! reviewed =
                    InstallationUsePlanRead.verified primaryOwner transaction witness planId ct

                match reviewed with
                | Some value when
                    value.ActivationId = activationId && value.Plan.Canonical = plan.Canonical
                    ->
                    ()
                | _ -> invalidOp "Published activation plan replay diverged."

                do! transaction.RollbackAsync(ct)
                return InstallationUsePlanOutcome.Published(planId, activationId)
            else
                started.Value <- true
                let intent = witness.BeginAuthority(planId, plan.Canonical, None)
                insert primaryOwner transaction planId activationId plan intent
                do! transaction.CommitAsync(CancellationToken.None)
                witness.SettleAuthority(planId, intent) |> ignore
                return InstallationUsePlanOutcome.Published(planId, activationId)
        }

    let publish
        (primaryOwner: NpgsqlConnection)
        (witness: WitnessProtocol)
        (plan: BackupHealthActivationPlan)
        (ct: CancellationToken)
        =
        task {
            let planId, activationId =
                try
                    identities plan
                with _ ->
                    Guid.Empty, Guid.Empty

            let started = ref false

            try
                if planId = Guid.Empty || not (valid plan) then
                    return InstallationUsePlanOutcome.Refused
                else
                    OwnerConnection.requireIdentity primaryOwner
                    SchemaBaseline.requireCurrent primaryOwner
                    witness.Admit()
                    return! underLock primaryOwner witness plan planId activationId started ct
            with _ ->
                return
                    if started.Value then
                        InstallationUsePlanOutcome.Unconfirmed planId
                    else
                        InstallationUsePlanOutcome.Refused
        }
