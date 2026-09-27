namespace ClaimCore.Postgres

open System
open System.IO
open System.Security.Cryptography
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

[<RequireQualifiedAccess>]
type internal AuthorityWriteOutcome =
    | Applied of eventId: Guid * revision: int64
    | Refused
    | Unconfirmed of eventId: Guid

module internal ActorGrantWrite =
    let private matchesInstallation
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT installation_id,lineage_id,witness_epoch FROM claimcore.installation_lineage WHERE singleton",
                    connection,
                    transaction
                )

            let! result = command.ExecuteReaderAsync()
            use reader = result
            let! found = reader.ReadAsync()
            let identity = witness.Identity

            return
                found
                && reader.GetGuid(0) = identity.InstallationId
                && reader.GetGuid(1) = identity.LineageId
                && reader.GetInt64(2) = identity.Epoch
                && not (reader.Read())
        }

    let private executeOne connection transaction sql bind =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            bind command
            let! count = command.ExecuteNonQueryAsync()

            if count <> 1 then
                raise (InvalidDataException("Authority mutation affected an unexpected row count."))
        }

    let private eventSql =
        "INSERT INTO claimcore.actor_authority_events "
        + "(revision,event_id,action_name,target_actor_id,approver_actor_id,canonical_action,"
        + "candidate_sha256,witness_sequence,witness_epoch,witness_entry_hash) "
        + "VALUES (@revision,@event,@action,@target,@approver,@canonical,@digest,@sequence,@epoch,@hash)"

    let persistEvent
        connection
        transaction
        (action: ActorAuthorityAction)
        canonical
        (intent: WitnessIntent)
        =
        task {
            do!
                executeOne connection transaction eventSql (fun command ->
                    Sql.integer command "revision" action.Revision
                    Sql.uuid command "event" action.EventId
                    Sql.text command "action" action.ActionName
                    Sql.uuid command "target" action.TargetActorId
                    Sql.optional command "approver" NpgsqlDbType.Uuid action.ApproverActorId
                    Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
                    Sql.add command "digest" NpgsqlDbType.Bytea (box intent.CandidateHash)
                    Sql.integer command "sequence" intent.Ticket.Sequence
                    Sql.integer command "epoch" intent.Ticket.Epoch
                    Sql.add command "hash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash))

            do!
                executeOne
                    connection
                    transaction
                    "UPDATE claimcore.authority_tip SET revision=@next WHERE singleton AND revision=@previous"
                    (fun command ->
                        Sql.integer command "next" action.Revision
                        Sql.integer command "previous" (action.Revision - 1L))
        }

    let run
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (action: ActorAuthorityAction)
        (apply: unit -> Task<unit>)
        =
        task {
            let! correct = matchesInstallation connection transaction witness

            if not correct then
                return AuthorityWriteOutcome.Refused
            else
                let canonical = ActorGrantCandidate.encode action

                let subjectCaseId =
                    match action.Grant with
                    | Some { Scope = GrantScope.Case caseId } -> Some caseId
                    | _ -> None

                try
                    try
                        let intent =
                            witness.BeginAuthority(action.EventId, canonical, subjectCaseId)

                        try
                            do! apply ()
                            do! persistEvent connection transaction action canonical intent
                            do! transaction.CommitAsync()
                            witness.SettleAuthority(action.EventId, intent) |> ignore
                            return AuthorityWriteOutcome.Applied(action.EventId, action.Revision)
                        with _ ->
                            return AuthorityWriteOutcome.Unconfirmed action.EventId
                    with _ ->
                        return AuthorityWriteOutcome.Unconfirmed action.EventId
                finally
                    CryptographicOperations.ZeroMemory(canonical)
        }

    let insertActor connection transaction actorId principal revision =
        let kind, issuer, stableValue = PrincipalKey.storageParts principal

        executeOne
            connection
            transaction
            ("INSERT INTO claimcore.actors "
             + "(actor_id,principal_kind,issuer,principal_value,enabled,changed_revision) "
             + "VALUES (@actor,@kind,@issuer,@value,true,@revision)")
            (fun command ->
                Sql.uuid command "actor" actorId
                Sql.text command "kind" kind
                Sql.text command "issuer" issuer
                Sql.text command "value" stableValue
                Sql.integer command "revision" revision)

    let setEnabled connection transaction actorId enabled revision =
        executeOne
            connection
            transaction
            ("UPDATE claimcore.actors SET enabled=@enabled,changed_revision=@revision "
             + "WHERE actor_id=@actor AND enabled<>@enabled")
            (fun command ->
                Sql.uuid command "actor" actorId
                Sql.add command "enabled" NpgsqlDbType.Boolean (box enabled)
                Sql.integer command "revision" revision)

    let setGrant connection transaction actorId (grant: ActorGrant) active revision =
        let kind, caseId = ActorGrantCandidate.scope grant.Scope
        let role = ActorGrantCandidate.roleName grant.Role

        executeOne
            connection
            transaction
            ("INSERT INTO claimcore.actor_grants "
             + "(actor_id,scope_kind,scope_case_id,role_name,active,changed_revision) "
             + "VALUES (@actor,@kind,@caseId,@role,@active,@revision) "
             + "ON CONFLICT (actor_id,scope_kind,scope_case_id,role_name) "
             + "DO UPDATE SET active=EXCLUDED.active,changed_revision=EXCLUDED.changed_revision "
             + "WHERE claimcore.actor_grants.active<>EXCLUDED.active")
            (fun command ->
                Sql.uuid command "actor" actorId
                Sql.text command "kind" kind
                Sql.uuid command "caseId" caseId
                Sql.text command "role" role
                Sql.add command "active" NpgsqlDbType.Boolean (box active)
                Sql.integer command "revision" revision)
