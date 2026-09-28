namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open DataAuditCommon

/// Reconstructs a human location signer holder's enabled/custodian authority at the
/// publication's captured global actor revision, not their possibly revoked current role.
module internal DataAuditExternalPublicationHolder =
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

    let private human connection transaction actor =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT principal_kind FROM claimcore.actors WHERE actor_id=@actor",
                    connection,
                    transaction
                )

            Sql.uuid command "actor" actor
            let! value = command.ExecuteScalarAsync()

            match value with
            | :? string as kind when kind = "HUMAN" -> return ()
            | _ -> corrupt ()
        }

    let private capture
        caseId
        (action: ActorAuthorityAction)
        (auditor: bool option, steward: bool option, local: bool option)
        =
        let active = action.ActionName <> "REVOKE_ROLE"

        match action.Grant with
        | Some {
                   Role = Role.AuditorCustodian
                   Scope = GrantScope.Installation
               } when auditor.IsNone -> Some active, steward, local
        | Some {
                   Role = Role.DataSteward
                   Scope = GrantScope.Installation
               } when steward.IsNone -> auditor, Some active, local
        | Some {
                   Role = Role.DataSteward
                   Scope = GrantScope.Case target
               } when target = caseId && local.IsNone -> auditor, steward, Some active
        | _ -> auditor, steward, local

    let verify connection transaction actor caseId revision (ct: CancellationToken) =
        task {
            do! human connection transaction actor
            let mutable before = revision
            let mutable enabled: bool option = None
            let mutable auditor: bool option = None
            let mutable steward: bool option = None
            let mutable local: bool option = None
            let mutable more = true

            while more
                  && (enabled.IsNone
                      || (auditor <> Some true && steward <> Some true && local <> Some true)) do
                let! rows = page connection transaction actor before ct

                for action in rows do
                    if enabled.IsNone then
                        enabled <- action.Enabled

                    let nextAuditor, nextSteward, nextLocal =
                        capture caseId action (auditor, steward, local)

                    auditor <- nextAuditor
                    steward <- nextSteward
                    local <- nextLocal

                match Array.tryLast rows with
                | Some last when rows.Length = 50 -> before <- last.Revision - 1L
                | _ -> more <- false

            if
                enabled <> Some true
                || (auditor <> Some true && steward <> Some true && local <> Some true)
            then
                corrupt ()
        }
