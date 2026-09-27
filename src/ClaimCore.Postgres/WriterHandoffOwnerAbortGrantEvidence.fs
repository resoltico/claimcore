namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Application

module internal WriterHandoffOwnerAbortGrantEvidence =
    let verifyGrantEvent connection transaction actor revision =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT event_id,action_name,approver_actor_id,canonical_action "
                    + "FROM claimcore.actor_authority_events WHERE revision=@revision "
                    + "AND target_actor_id=@actor",
                    connection,
                    transaction
                )

            Sql.integer command "revision" revision
            Sql.uuid command "actor" actor
            use reader = command.ExecuteReader()

            if not (reader.Read()) then
                invalidOp "Historical owner grant event is absent."

            let eventId = reader.GetGuid(0)
            let actionName = reader.GetString(1)
            let approver = if reader.IsDBNull(2) then None else Some(reader.GetGuid(2))
            let canonical = reader.GetFieldValue<byte array>(3)

            let action =
                ActorGrantCandidate.decodeStored
                    canonical
                    revision
                    eventId
                    actionName
                    actor
                    approver
                |> Option.defaultWith (fun () -> invalidOp "Historical owner grant is invalid.")

            if reader.Read() then
                invalidOp "Historical owner grant is duplicated."

            match action.Grant with
            | Some {
                       Role = Role.Owner
                       Scope = GrantScope.Installation
                   } when actionName = "GRANT_ROLE" || actionName = "PROVISION_INITIAL_OWNER" -> ()
            | _ -> invalidOp "Historical owner grant does not match."
        }
