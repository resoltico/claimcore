namespace ClaimCore.Database

open System
open Npgsql

module internal DatabaseRestoreOwnerRoster =
    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (approvers: RestoreOwnerApprover list)
        =
        use command =
            new NpgsqlCommand(
                "SELECT a.actor_id,g.changed_revision,e.event_id "
                + "FROM claimcore.actors a JOIN claimcore.actor_grants g "
                + "ON g.actor_id=a.actor_id LEFT JOIN claimcore.actor_authority_events e "
                + "ON e.revision=g.changed_revision AND e.target_actor_id=a.actor_id "
                + "AND e.action_name IN ('PROVISION_INITIAL_OWNER','GRANT_ROLE') "
                + "WHERE a.enabled AND a.principal_kind='HUMAN' AND g.active "
                + "AND g.scope_kind='INSTALLATION' "
                + "AND g.scope_case_id='00000000-0000-0000-0000-000000000000' "
                + "AND g.role_name='OWNER' ORDER BY a.actor_id",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()
        let found = ResizeArray<RestoreOwnerApprover>()

        while reader.Read() do
            if found.Count >= 1000 || reader.IsDBNull(2) then
                invalidOp "Restored owner roster has no exact witnessed grant event."

            found.Add(
                {
                    ActorId = reader.GetGuid(0)
                    GrantRevision = reader.GetInt64(1)
                    ApprovalEventId = reader.GetGuid(2)
                }
            )

        let identity (value: RestoreOwnerApprover) =
            value.ActorId, value.GrantRevision, value.ApprovalEventId

        let claimed = approvers |> List.map identity |> List.sort
        let observed = found |> Seq.map identity |> Seq.toList |> List.sort

        if found.Count < 2 || observed <> claimed then
            invalidOp "Restored owner roster differs from the signed report."
