namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open DataAuditCommon

/// Reconstructs the human OWNER grant at approval time. A later revocation does not
/// invalidate an already witnessed draft, but a then-absent grant does.
module internal DataAuditCopyAdoptionOwnerRole =
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

    let private capture
        caseId
        (action: ActorAuthorityAction)
        (installation: bool option)
        (local: bool option)
        =
        let active = action.ActionName <> "REVOKE_ROLE"

        match action.Grant with
        | Some {
                   Role = Role.Owner
                   Scope = GrantScope.Installation
               } when installation.IsNone -> Some active, local
        | Some {
                   Role = Role.Owner
                   Scope = GrantScope.Case target
               } when target = caseId && local.IsNone -> installation, Some active
        | _ -> installation, local

    let private human connection transaction actor =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT principal_kind FROM claimcore.actors WHERE actor_id=@actor",
                    connection,
                    transaction
                )

            Sql.uuid command "actor" actor
            let! kind = command.ExecuteScalarAsync()

            match kind with
            | :? string as value when value = "HUMAN" -> return ()
            | _ -> corrupt ()
        }

    let verify connection transaction actor caseId revision (ct: CancellationToken) =
        task {
            do! human connection transaction actor
            let mutable before = revision
            let mutable installation: bool option = None
            let mutable local: bool option = None
            let mutable enabled: bool option = None
            let mutable more = true

            while more && (enabled.IsNone || (installation <> Some true && local <> Some true)) do
                let! rows = page connection transaction actor before ct

                for action in rows do
                    let nextInstallation, nextLocal = capture caseId action installation local
                    installation <- nextInstallation
                    local <- nextLocal

                    if enabled.IsNone then
                        enabled <- action.Enabled

                match Array.tryLast rows with
                | Some last when rows.Length = 50 -> before <- last.Revision - 1L
                | _ -> more <- false

            if enabled <> Some true || (installation <> Some true && local <> Some true) then
                corrupt ()
        }
