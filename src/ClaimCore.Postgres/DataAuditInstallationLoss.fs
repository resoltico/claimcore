namespace ClaimCore.Postgres

open System.Threading
open Npgsql
open ClaimCore.Witness
open DataAuditCommon

/// W0 without W1 is a pending fenced incident, never a clean restore.
module internal DataAuditInstallationLoss =
    let verify
        (connection: NpgsqlConnection)
        transaction
        (witness: WitnessProtocol)
        (tip: Snapshot)
        (ticket: Ticket)
        (_: CancellationToken)
        =
        let row, value =
            DataAuditInstallationLossWitness.read witness tip ticket.OperationId

        if
            tip.LossRetirementId <> Some row.RetirementId
            || tip.LossRetirementIntentSequence <> Some row.IntentSequence
            || tip.LossRetirementIntentHash <> Some row.IntentHash
        then
            corrupt ()

        DataAuditInstallationLossPrimary.signatures connection transaction row

        match ticket.Phase with
        | Intent ->
            DataAuditInstallationLossWitness.intent witness row ticket

            if row.SettlementSequence.IsSome && not tip.LossRetired then
                corrupt ()

            let retained =
                DataAuditInstallationLossPrimary.receipt connection transaction row value false

            if retained then
                DataAuditInstallationLossPrimary.denials connection transaction row

            if not tip.LossRetirementPending && not tip.LossRetired then
                corrupt ()
        | SettledAuthority ->
            DataAuditInstallationLossWitness.settlement witness tip row ticket

            if
                not (DataAuditInstallationLossPrimary.receipt connection transaction row value true)
            then
                corrupt ()

            DataAuditInstallationLossPrimary.denials connection transaction row
        | _ -> corrupt ()

        if
            value.OperationSet = InstallationLossOperationSet.Unknown
            && value.KnownOperationCount <> 0
        then
            corrupt ()
