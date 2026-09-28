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
    let private readSettled (witness: WitnessProtocol) =
        let stored =
            witness.ReadDataUseActivation()
            |> Option.defaultWith (fun () -> invalidOp "Settled real-data activation is absent.")

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

        witness.VerifyHistoricalTip(record.ExpectedWitnessSequence, record.ExpectedWitnessHash)

        WriterActivationWitness.verifyHistorical
            witness
            stored.EventId
            stored.Canonical
            (stored.IntentSequence, stored.IntentHash)
            (stored.SettlementSequence, stored.SettlementHash)
        |> ignore

        stored, record

    let private stateMatches
        (record: InstallationUseActivationRecord)
        (witness: WitnessProtocol)
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
        && witness.Snapshot().Use.ActivationEventId = Some record.EventId

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

            let approvals =
                InstallationUseActivationApprovals.verifyHistorical
                    primary
                    transaction
                    witness
                    plan
                    record

            InstallationUseActivationPrimary.insertHistorical
                primary
                transaction
                record
                stored.Canonical
                intent
                settled

            InstallationUseActivationApprovals.consume primary transaction record.EventId approvals
            InstallationUseActivationPrimary.release primary transaction record.EventId settled
            do! transaction.CommitAsync(CancellationToken.None)
        }

    let private pairedOutcome
        (primary: NpgsqlConnection)
        (witness: WitnessProtocol)
        (stored: WitnessDataUseActivation)
        (record: InstallationUseActivationRecord)
        =
        let paired = InstallationUseScopeRead.requirePair primary witness

        if
            paired.Phase = InstallationUsePhase.Active
            && paired.ActivationEventId = Some record.EventId
            && paired.ActivationSequence = Some stored.SettlementSequence
            && paired.ActivationHash = Some stored.SettlementHash
        then
            InstallationUseActivationOutcome.Activated(
                record.EventId,
                stored.SettlementSequence,
                stored.SettlementHash
            )
        else
            InstallationUseActivationOutcome.Unconfirmed record.EventId

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
                InstallationUseActivationPrimary.requireExisting
                    primary
                    transaction
                    record
                    stored.Canonical
                    intent
                    settled

                do! transaction.RollbackAsync(CancellationToken.None)

            return pairedOutcome primary witness stored record
        }

    let run (primaryOwner: NpgsqlConnection) (witness: WitnessProtocol) (ct: CancellationToken) =
        task {
            let mutable eventId = Guid.Empty
            let started = ref false

            try
                OwnerConnection.requireIdentity primaryOwner
                SchemaBaseline.requireCurrent primaryOwner
                witness.AdmitReadOnly()
                let stored, record = readSettled witness
                eventId <- record.EventId
                use transaction = primaryOwner.BeginTransaction(IsolationLevel.ReadCommitted)
                let! _ = ActorGrantRead.lockRevision primaryOwner transaction true ct

                let identity, generation, scope, phase, priorId, _, _ =
                    InstallationUseActivationPrimary.state primaryOwner transaction

                if not (stateMatches record witness identity generation scope phase priorId) then
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
