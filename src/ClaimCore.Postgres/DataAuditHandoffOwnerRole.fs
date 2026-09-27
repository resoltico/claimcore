namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open DataAuditCommon

/// Reconstructs the one actor's OWNER and enabled states at a historical approval revision.
module internal DataAuditHandoffOwnerRole =
    let private page connection transaction actor before (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT revision,event_id,action_name,target_actor_id,approver_actor_id,"
                    + "canonical_action FROM claimcore.actor_authority_events "
                    + "WHERE target_actor_id=@actor AND revision<=@before "
                    + "ORDER BY revision DESC LIMIT 50",
                    connection,
                    transaction
                )

            Sql.uuid command "actor" actor
            Sql.integer command "before" before
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<ActorAuthorityAction>()

            while reader.Read() do
                let approver = if reader.IsDBNull(4) then None else Some(reader.GetGuid(4))

                let action =
                    ActorGrantCandidate.decodeStored
                        (reader.GetFieldValue<byte array>(5))
                        (reader.GetInt64(0))
                        (reader.GetGuid(1))
                        (reader.GetString(2))
                        (reader.GetGuid(3))
                        approver
                    |> Option.defaultWith corrupt

                rows.Add action

            return rows.ToArray()
        }

    let verify connection transaction actor revision (ct: CancellationToken) =
        task {
            let mutable before = revision
            let mutable owner: bool option = None
            let mutable enabled: bool option = None
            let mutable more = true

            while more && (owner.IsNone || enabled.IsNone) do
                let! rows = page connection transaction actor before ct

                for action in rows do
                    if owner.IsNone then
                        match action.Grant with
                        | Some {
                                   Role = Role.Owner
                                   Scope = GrantScope.Installation
                               } -> owner <- Some(action.ActionName <> "REVOKE_ROLE")
                        | _ -> ()

                    if enabled.IsNone then
                        enabled <- action.Enabled

                match Array.tryLast rows with
                | Some last when rows.Length = 50 -> before <- last.Revision - 1L
                | _ -> more <- false

            if owner <> Some true || enabled <> Some true then
                corrupt ()
        }
