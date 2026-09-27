namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open DataAuditCommon

/// Compare current actor/grant cache rows with the pure replay of witnessed authority events.
/// The replay map is bounded by current authority cardinality, not claimant event history.
module internal DataAuditAuthorityProjection =
    let private actors
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (projection: AuthorityProjection)
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT actor_id,principal_kind,issuer,principal_value,enabled,changed_revision FROM claimcore.actors ORDER BY actor_id",
                    connection,
                    transaction
                )

            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let mutable count = 0
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(cancellationToken)
                reading <- found

                if found then
                    let actorId = reader.GetGuid(0)

                    let principal =
                        PrincipalKey.fromStorage
                            (reader.GetString(1))
                            (reader.GetString(2))
                            (reader.GetString(3))
                        |> Result.defaultWith (fun _ -> corrupt ())

                    match projection.Actors |> Map.tryFind actorId with
                    | Some expected when
                        expected.Principal = principal
                        && expected.Enabled = reader.GetBoolean(4)
                        && expected.ChangedRevision = reader.GetInt64(5)
                        ->
                        ()
                    | _ -> corrupt ()

                    count <- count + 1

            if count <> projection.Actors.Count then
                corrupt ()

            return int64 count
        }

    let private grants
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (projection: AuthorityProjection)
        (cancellationToken: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT actor_id,scope_kind,scope_case_id,role_name,active,changed_revision FROM claimcore.actor_grants ORDER BY actor_id,scope_kind,scope_case_id,role_name",
                    connection,
                    transaction
                )

            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let mutable count = 0
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(cancellationToken)
                reading <- found

                if found then
                    let actorId = reader.GetGuid(0)

                    let grant =
                        ActorGrantCandidate.grantFromStorage
                            (reader.GetString(1))
                            (reader.GetGuid(2))
                            (reader.GetString(3))
                        |> Option.defaultWith corrupt

                    match projection.Actors |> Map.tryFind actorId with
                    | Some actor ->
                        match actor.Grants |> Map.tryFind grant with
                        | Some expected when
                            expected.Active = reader.GetBoolean(4)
                            && expected.ChangedRevision = reader.GetInt64(5)
                            ->
                            ()
                        | _ -> corrupt ()
                    | _ -> corrupt ()

                    count <- count + 1

            let expected =
                projection.Actors
                |> Map.toSeq
                |> Seq.sumBy (fun (_, actor) -> actor.Grants.Count)

            if count <> expected then
                corrupt ()

            return int64 count
        }

    let verify connection transaction projection cancellationToken =
        task {
            let! actorCount = actors connection transaction projection cancellationToken
            let! grantCount = grants connection transaction projection cancellationToken
            return actorCount, grantCount
        }
