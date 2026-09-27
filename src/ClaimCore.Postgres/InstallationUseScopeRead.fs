namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Witness

/// Exact primary/witness use-scope agreement; a cached health or phase flag is not authority.
module internal InstallationUseScopeRead =
    let requirePair (primary: NpgsqlConnection) (witness: WitnessProtocol) =
        use command =
            new NpgsqlCommand(
                "SELECT installation_id,lineage_id,witness_epoch,data_use_scope,data_use_phase,"
                + "data_use_activation_event_id,data_use_activation_sequence,"
                + "data_use_activation_hash FROM claimcore.installation_lineage WHERE singleton",
                primary
            )

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            invalidOp "Installation use scope is unavailable."

        let identity = reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2)

        let state =
            {
                Scope = InstallationUse.parseScope (reader.GetString(3))
                Phase = InstallationUse.parsePhase (reader.GetString(4))
                ActivationEventId = if reader.IsDBNull(5) then None else Some(reader.GetGuid(5))
                ActivationSequence =
                    if reader.IsDBNull(6) then
                        None
                    else
                        Some(reader.GetInt64(6))
                ActivationHash =
                    if reader.IsDBNull(7) then
                        None
                    else
                        Some(reader.GetFieldValue<byte array>(7))
            }

        if reader.Read() then
            invalidOp "Installation use scope is ambiguous."

        reader.Close()
        let observed = witness.Snapshot()

        if
            identity
            <> (observed.Identity.InstallationId,
                observed.Identity.LineageId,
                observed.Identity.Epoch)
            || state.Scope <> observed.Use.Scope
            || state.Phase <> observed.Use.Phase
            || state.ActivationEventId <> observed.Use.ActivationEventId
            || state.ActivationSequence <> observed.Use.ActivationSequence
            || state.ActivationHash <> observed.Use.ActivationHash
        then
            invalidOp "Primary and witness installation use authority diverged."

        state
