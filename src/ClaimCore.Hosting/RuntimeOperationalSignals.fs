namespace ClaimCore.Hosting

open System
open System.Diagnostics.Metrics
open System.Collections.Generic
open System.Collections.Concurrent
open ClaimCore.Postgres

/// Fixed diagnostic vocabulary; no request, case, credential or provider text is recorded.
module internal RuntimeOperationalSignals =
    let private meter = new Meter("ClaimCore.Runtime")

    let private reportedWitness =
        ConcurrentDictionary<struct (WitnessFailureStage * WitnessFailureCause), byte>()

    let private witnessFailures =
        meter.CreateCounter<int64>("claimcore.witness.failures")

    let private quarantines = meter.CreateCounter<int64>("claimcore.audit.quarantines")

    let witness stage cause =
        let stageCode =
            match stage with
            | WitnessFailureStage.Read -> "read"
            | WitnessFailureStage.Append -> "append"
            | WitnessFailureStage.Settlement -> "settlement"

        let causeCode =
            match cause with
            | WitnessFailureCause.PendingEvidence -> "pending_evidence"
            | WitnessFailureCause.Transport -> "transport"
            | WitnessFailureCause.Schema -> "schema"
            | WitnessFailureCause.Authority -> "authority"
            | WitnessFailureCause.Integrity -> "integrity"
            | WitnessFailureCause.Unexpected -> "unexpected"

        witnessFailures.Add(
            1L,
            KeyValuePair("stage", box stageCode),
            KeyValuePair("cause", box causeCode)
        )

        if reportedWitness.TryAdd(struct (stage, cause), 0uy) then
            Console.Error.WriteLine(
                "ClaimCore witness failure: stage="
                + stageCode
                + " cause="
                + causeCode
                + "; preserve operation identity and reconcile exact evidence."
            )

    let audit overdue =
        let reason = if overdue then "overdue" else "failed"
        quarantines.Add(1L, KeyValuePair("reason", box reason))

        Console.Error.WriteLine(
            "ClaimCore audit quarantine: "
            + reason
            + "; actor access is closed. Reconcile with owner verify-data before reopening."
        )
