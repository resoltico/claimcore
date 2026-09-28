namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open DataAuditCommon

/// Replays the verifier holder's HUMAN custodian/steward authority at the witnessed VERIFY.
module internal DataAuditCopyPhysicalVerifierRole =
    let private human connection transaction actor =
        use command =
            new NpgsqlCommand(
                "SELECT principal_kind FROM claimcore.actors WHERE actor_id=@actor",
                connection,
                transaction
            )

        Sql.uuid command "actor" actor

        match command.ExecuteScalar() with
        | :? string as value when value = "HUMAN" -> ()
        | _ -> corrupt ()

    let private page connection transaction actor before (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT revision,event_id,action_name,target_actor_id,"
                    + "approver_actor_id,canonical_action,witness_sequence "
                    + "FROM claimcore.actor_authority_events WHERE target_actor_id=@actor "
                    + "AND witness_sequence<=@before ORDER BY witness_sequence DESC LIMIT 50",
                    connection,
                    transaction
                )

            Sql.uuid command "actor" actor
            Sql.integer command "before" before
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<ActorAuthorityAction * int64>()

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

                rows.Add(action, reader.GetInt64(6))

            return rows.ToArray()
        }

    let private capture
        (action: ActorAuthorityAction)
        (auditor: bool option)
        (steward: bool option)
        =
        let active = action.ActionName <> "REVOKE_ROLE"

        match action.Grant with
        | Some {
                   Role = Role.AuditorCustodian
                   Scope = GrantScope.Installation
               } when auditor.IsNone -> Some active, steward
        | Some {
                   Role = Role.DataSteward
                   Scope = GrantScope.Installation
               } when steward.IsNone -> auditor, Some active
        | _ -> auditor, steward

    let verify connection transaction actor cutoff (ct: CancellationToken) =
        task {
            human connection transaction actor
            let mutable before = cutoff
            let mutable auditor: bool option = None
            let mutable steward: bool option = None
            let mutable enabled: bool option = None
            let mutable more = true

            while more
                  && enabled <> Some false
                  && (enabled.IsNone || (auditor <> Some true && steward <> Some true)) do
                let! rows = page connection transaction actor before ct

                for action, _ in rows do
                    let nextAuditor, nextSteward = capture action auditor steward
                    auditor <- nextAuditor
                    steward <- nextSteward

                    if enabled.IsNone then
                        enabled <- action.Enabled

                match Array.tryLast rows with
                | Some(_, sequence) when rows.Length = 50 -> before <- sequence - 1L
                | _ -> more <- false

            if enabled <> Some true || (auditor <> Some true && steward <> Some true) then
                corrupt ()
        }
