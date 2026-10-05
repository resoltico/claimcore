namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Witness
open WitnessProtocolReconciliation

/// Repair only the exact primary projection of a previously settled witness activation.
/// This never appends a fresh witness intent or accepts a new health certificate.
module internal InstallationUseActivationReconcile =
    let private readSettled (witness: WitnessProtocol) ct =
        task {
            let! observed = witness.ReadDataUseActivation(ct)

            let stored =
                observed
                |> Option.defaultWith (fun () ->
                    invalidOp "Settled real-data activation is absent.")

            let record =
                InstallationUseActivationCodec.decode stored.Canonical
                |> Option.defaultWith (fun () -> invalidOp "Settled real-data action is invalid.")

            if
                record.EventId <> stored.EventId
                || record.InstallationId <> witness.Identity.InstallationId
                || record.LineageId <> witness.Identity.LineageId
                || record.Epoch <> witness.Identity.Epoch
                || record.WriterGeneration <> stored.WriterGeneration
                || record.ExpectedWitnessSequence + 1L <> stored.IntentSequence
            then
                invalidOp "Historical activation identity diverged."

            do!
                witness.VerifyHistoricalTip(
                    record.ExpectedWitnessSequence,
                    record.ExpectedWitnessHash,
                    ct
                )

            let! _ =
                WriterActivationWitness.verifyHistorical
                    witness
                    stored.EventId
                    stored.Canonical
                    (stored.IntentSequence, stored.IntentHash)
                    (stored.SettlementSequence, stored.SettlementHash)
                    ct

            return stored, record
        }

    let private stateMatches
        (record: InstallationUseActivationRecord)
        (snapshot: Snapshot)
        identity
        generation
        scope
        phase
        priorId
        =
        identity = (record.InstallationId, record.LineageId, record.Epoch)
        && generation = record.WriterGeneration
        && scope = InstallationUseScope.RealData
        && (phase = InstallationUsePhase.BootstrapNoCases
            || (phase = InstallationUsePhase.Active && priorId = Some record.EventId))
        && snapshot.Use.ActivationEventId = Some record.EventId

    let private historicalPlan
        primary
        transaction
        witness
        (record: InstallationUseActivationRecord)
        ct
        =
        task {
            let! published =
                InstallationUsePlanRead.verified primary transaction witness record.PlanId ct

            let plan =
                published
                |> Option.defaultWith (fun () -> invalidOp "Historical activation plan is missing.")

            if
                plan.ActivationId <> record.EventId
                || Convert.FromHexString(plan.Plan.PlanSha256) <> record.PlanSha256
                || Convert.FromHexString(plan.Plan.PolicySha256) <> record.PolicySha256
            then
                invalidOp "Historical activation plan changed."

            return plan
        }

    let private repairMissing
        (primary: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (stored: WitnessDataUseActivation)
        (record: InstallationUseActivationRecord)
        intent
        settled
        (ct: CancellationToken)
        =
        task {
            let! plan = historicalPlan primary transaction witness record ct

            let! approvals =
                InstallationUseActivationApprovals.verifyHistorical
                    primary
                    transaction
                    witness
                    plan
                    record
                    ct

            do!
                InstallationUseActivationPrimary.insertHistorical
                    primary
                    transaction
                    record
                    stored.Canonical
                    intent
                    settled
                    CancellationToken.None

            do!
                InstallationUseActivationPrimary.complete
                    primary
                    transaction
                    record.EventId
                    approvals
                    settled
                    CancellationToken.None

            do! transaction.CommitAsync(CancellationToken.None)
        }

    let private pairedOutcome
        (primary: NpgsqlConnection)
        (witness: WitnessProtocol)
        (stored: WitnessDataUseActivation)
        (record: InstallationUseActivationRecord)
        ct
        =
        task {
            let! paired = InstallationUseScopeRead.requirePair primary witness ct

            if
                paired.Phase = InstallationUsePhase.Active
                && paired.ActivationEventId = Some record.EventId
                && paired.ActivationSequence = Some stored.SettlementSequence
                && paired.ActivationHash = Some stored.SettlementHash
            then
                return
                    InstallationUseActivationOutcome.Activated(
                        record.EventId,
                        stored.SettlementSequence,
                        stored.SettlementHash
                    )
            else
                return InstallationUseActivationOutcome.Unconfirmed record.EventId
        }

    let private repair
        (primary: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (stored: WitnessDataUseActivation)
        (record: InstallationUseActivationRecord)
        phase
        ct
        =
        task {
            let intent = stored.IntentSequence, stored.IntentHash
            let settled = stored.SettlementSequence, stored.SettlementHash

            if phase = InstallationUsePhase.BootstrapNoCases then
                do! repairMissing primary transaction witness stored record intent settled ct
            else
                do!
                    InstallationUseActivationPrimary.requireExisting
                        primary
                        transaction
                        record
                        stored.Canonical
                        intent
                        settled
                        ct

                do! transaction.RollbackAsync(CancellationToken.None)

            return! pairedOutcome primary witness stored record CancellationToken.None
        }

    let run (primaryOwner: NpgsqlConnection) (witness: WitnessProtocol) (ct: CancellationToken) =
        task {
            let mutable eventId = Guid.Empty
            let started = ref false

            try
                OwnerConnection.requireIdentity primaryOwner
                SchemaBaseline.requireCurrent primaryOwner
                do! witness.AdmitReadOnly(ct)
                use! _authorityFence = AuthorityOperationFence.acquireShared None primaryOwner ct
                let! stored, record = readSettled witness ct
                eventId <- record.EventId

                use! transaction =
                    primaryOwner.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)

                let! _ = ActorGrantRead.lockRevision primaryOwner transaction true ct

                let! identity, generation, scope, phase, priorId, _, _ =
                    InstallationUseActivationPrimary.state primaryOwner transaction ct

                let! snapshot = witness.Snapshot(ct)

                if not (stateMatches record snapshot identity generation scope phase priorId) then
                    return InstallationUseActivationOutcome.Refused
                else
                    started.Value <- true
                    return! repair primaryOwner transaction witness stored record phase ct
            with _ ->
                return
                    if started.Value then
                        InstallationUseActivationOutcome.Unconfirmed eventId
                    else
                        InstallationUseActivationOutcome.Refused
        }
