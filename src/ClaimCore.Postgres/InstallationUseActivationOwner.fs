namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Witness

type internal InstallationUseHealthVerifier =
    NpgsqlConnection
        -> NpgsqlTransaction
        -> WitnessProtocol
        -> ReviewedDeploymentProfile
        -> BackupHealthQualifiedEvidence
        -> unit

/// Owner-only one-way real-data activation. Witness settlement precedes the primary projection;
/// any ambiguous phase remains quarantined until this exact event is reconciled.
module internal InstallationUseActivationOwner =
    let private eventIdOrEmpty (plan: BackupHealthActivationPlan) =
        try
            InstallationUseActivationCandidate.eventIdFromPlan
                plan.InstallationId
                (Convert.FromHexString plan.PlanSha256)
        with _ ->
            Guid.Empty

    let private approvedProof
        (primary: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (profile: ReviewedDeploymentProfile)
        (verifyHealth: InstallationUseHealthVerifier)
        (proof: BackupHealthQualifiedEvidence)
        (plan: BackupHealthActivationPlan)
        firstId
        secondId
        now
        snapshot
        (ct: CancellationToken)
        =
        task {
            let planId =
                InstallationUseActivationCandidate.planIdFromDigest
                    plan.InstallationId
                    (Convert.FromHexString plan.PlanSha256)

            let! published =
                InstallationUsePlanRead.verified primary transaction witness planId ct

            match published with
            | Some value when value.Plan.Canonical = plan.Canonical ->
                let pair =
                    InstallationUseActivationApprovals.verify
                        primary
                        transaction
                        witness
                        value
                        firstId
                        secondId
                        now

                verifyHealth primary transaction witness profile proof

                return
                    if pair.Second.SettlementSequence <= snapshot.TipSequence then
                        Some pair
                    else
                        None
            | _ -> return None
        }

    let private currentPhase
        primary
        transaction
        (witness: WitnessProtocol)
        (proof: BackupHealthQualifiedEvidence)
        (plan: BackupHealthActivationPlan)
        =
        let identity, generation, scope, phase, priorId, priorSequence, priorHash =
            InstallationUseActivationPrimary.state primary transaction

        let state =
            InstallationUseActivationPreflight.state scope phase priorId priorSequence priorHash

        let snapshot = witness.Snapshot()
        let now = Sql.databaseNowSync primary transaction

        let matches =
            InstallationUseActivationPreflight.phaseMatches
                proof
                witness
                plan
                state
                generation
                identity
                snapshot
                now

        matches, now, snapshot

    let private phaseUnderLock
        (primary: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (profile: ReviewedDeploymentProfile)
        (verifyHealth: InstallationUseHealthVerifier)
        (proof: BackupHealthQualifiedEvidence)
        (plan: BackupHealthActivationPlan)
        firstId
        secondId
        (ct: CancellationToken)
        =
        task {
            let! _ = ActorGrantRead.lockRevision primary transaction true ct

            let matches, now, snapshot = currentPhase primary transaction witness proof plan

            if not matches then
                return None
            else
                // A loss decision cannot be bootstrapped after a failed audit has closed
                // actor authority. Require two independently held retirement keys now.
                InstallationLossRetirementSigners.requireReady
                    primary
                    transaction
                    witness
                    snapshot.TipSequence

                return!
                    approvedProof
                        primary
                        transaction
                        witness
                        profile
                        verifyHealth
                        proof
                        plan
                        firstId
                        secondId
                        now
                        snapshot
                        ct
        }

    let private inTransaction
        (primaryOwner: NpgsqlConnection)
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (profile: ReviewedDeploymentProfile)
        verifyHealth
        (proof: BackupHealthQualifiedEvidence)
        (plan: BackupHealthActivationPlan)
        firstApprovalId
        secondApprovalId
        (eventId: Guid)
        (started: bool ref)
        (ct: CancellationToken)
        =
        task {
            use! _authorityFence = AuthorityOperationFence.acquireShared None primaryOwner ct
            use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)

            let! approvals =
                phaseUnderLock
                    primaryOwner
                    transaction
                    witness
                    profile
                    verifyHealth
                    proof
                    plan
                    firstApprovalId
                    secondApprovalId
                    ct

            match approvals with
            | None -> return InstallationUseActivationOutcome.Refused
            | Some pair ->
                let canonical = InstallationUseActivationCandidate.encodeFinal plan proof pair

                try
                    return!
                        InstallationUseActivationCommit.settleAndPersist
                            primaryOwner
                            transaction
                            ownerWitnessConnection
                            witness
                            proof
                            plan
                            pair
                            eventId
                            canonical
                            started
                finally
                    CryptographicOperations.ZeroMemory(canonical)
        }

    /// Internal qualification seam; only isolated tests inject a verifier. Product callers use
    /// activate below, which always sources the reviewed build profile and real health proof.
    let internal activateWithReviewedProfile
        (profile: ReviewedDeploymentProfile)
        verifyHealth
        (primaryOwner: NpgsqlConnection)
        ownerWitnessConnection
        (witness: WitnessProtocol)
        (proof: BackupHealthQualifiedEvidence)
        (plan: BackupHealthActivationPlan)
        firstApprovalId
        secondApprovalId
        (ct: CancellationToken)
        =
        task {
            let eventId = eventIdOrEmpty plan

            if
                eventId = Guid.Empty
                || not (InstallationUseActivationPreflight.proofShape profile proof plan)
                || firstApprovalId = secondApprovalId
            then
                return InstallationUseActivationOutcome.Refused
            else
                let started = ref false

                try
                    OwnerConnection.requireIdentity primaryOwner
                    SchemaBaseline.requireCurrent primaryOwner
                    witness.AdmitReadOnly()

                    return!
                        inTransaction
                            primaryOwner
                            ownerWitnessConnection
                            witness
                            profile
                            verifyHealth
                            proof
                            plan
                            firstApprovalId
                            secondApprovalId
                            eventId
                            started
                            ct
                with _ ->
                    return
                        if started.Value then
                            InstallationUseActivationOutcome.Unconfirmed eventId
                        else
                            InstallationUseActivationOutcome.Refused
        }

    let activate
        primaryOwner
        ownerWitnessConnection
        witness
        proof
        plan
        firstApprovalId
        secondApprovalId
        ct
        =
        task {
            match ReviewedDeploymentRoot.current () with
            | None -> return InstallationUseActivationOutcome.Refused
            | Some profile ->
                try
                    return!
                        activateWithReviewedProfile
                            profile
                            InstallationUseActivationPreflight.healthUnderLock
                            primaryOwner
                            ownerWitnessConnection
                            witness
                            proof
                            plan
                            firstApprovalId
                            secondApprovalId
                            ct
                finally
                    CryptographicOperations.ZeroMemory(profile.PublicationRootKey)
        }
