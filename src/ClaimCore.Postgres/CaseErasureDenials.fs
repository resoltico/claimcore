namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

/// A bounded, repeatable scan binds every retained case-operation identity to its privacy fence.
/// The commitment key never enters SQL; this denial table retains no raw operation identity.
module internal CaseErasureDenials =
    let private pageSql =
        "SELECT operation_id,min(priority)::integer FROM ("
        + "SELECT operation_id,1 AS priority FROM claimcore.case_changes "
        + "WHERE case_id=@case AND operation_id>@after UNION ALL "
        + "SELECT operation_id,2 FROM claimcore.operation_revocations "
        + "WHERE case_id=@case AND operation_id>@after UNION ALL "
        + "SELECT p.operation_id,CASE WHEN EXISTS (SELECT 1 FROM "
        + "claimcore.request_submission_attempts a WHERE a.operation_id=p.operation_id) "
        + "THEN 3 ELSE 4 END FROM claimcore.request_preparations p "
        + "WHERE p.case_id=@case AND p.operation_id>@after UNION ALL "
        + "SELECT operation_id,4 FROM claimcore.recovery_artifact_exports "
        + "WHERE case_id=@case AND operation_id>@after) identities "
        + "GROUP BY operation_id ORDER BY operation_id LIMIT 50"

    let private readPage connection transaction caseId after (ct: CancellationToken) =
        task {
            use command = new NpgsqlCommand(pageSql, connection, transaction)
            Sql.uuid command "case" caseId
            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let items = ResizeArray<Guid * int>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(ct)
                reading <- found

                if found then
                    items.Add(reader.GetGuid(0), reader.GetInt32(1))

            return items |> Seq.toList
        }

    let private knowledge =
        function
        | 1 -> "ACCEPTED"
        | 2 -> "REVOKED"
        | 3 -> "ATTEMPT_UNCERTAIN"
        | 4 -> "PENDING"
        | _ -> invalidOp "Erasure operation knowledge is invalid."

    let private insert connection transaction caseId commitment priority (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.case_erasure_operation_denials "
                    + "(operation_commitment,case_id,knowledge) VALUES (@commitment,@case,@knowledge)",
                    connection,
                    transaction
                )

            Sql.add command "commitment" NpgsqlDbType.Bytea (box commitment)
            Sql.uuid command "case" caseId
            Sql.text command "knowledge" (knowledge priority)
            let! affected = command.ExecuteNonQueryAsync(ct)

            if affected <> 1 then
                invalidOp "Erasure operation denial was not retained."
        }

    let scan
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        caseId
        (commitments: ISuppressionCommitments)
        persist
        (ct: CancellationToken)
        =
        task {
            commitments.Admit()
            use aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            aggregate.AppendData(Encoding.ASCII.GetBytes("CLAIMCORE_ERASURE_DENIAL_SET_V1\000"))
            let mutable after = Guid.Empty
            let mutable count = 0L
            let mutable more = true

            while more do
                let! page = readPage connection transaction caseId after ct

                match List.tryLast page with
                | None -> more <- false
                | Some(last, _) ->
                    for operationId, priority in page do
                        let commitment = commitments.Operation operationId

                        if commitment.Length <> 32 then
                            invalidOp "Erasure operation commitment is invalid."

                        aggregate.AppendData(commitment)
                        aggregate.AppendData([| byte priority |])

                        if persist then
                            do! insert connection transaction caseId commitment priority ct

                        count <- count + 1L

                    after <- last

            return count, aggregate.GetHashAndReset()
        }

    let private requireStored
        connection
        transaction
        caseId
        (commitments: ISuppressionCommitments)
        operationId
        priority
        (ct: CancellationToken)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT knowledge FROM claimcore.case_erasure_operation_denials "
                    + "WHERE case_id=@case AND operation_commitment=@commitment",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId

            Sql.add
                command
                "commitment"
                NpgsqlDbType.Bytea
                (box (commitments.Operation operationId))

            let! stored = command.ExecuteScalarAsync(ct)

            if
                not (
                    String.Equals(
                        stored :?> string | null,
                        knowledge priority,
                        StringComparison.Ordinal
                    )
                )
            then
                invalidOp "Erasure operation denial differs from retained identity."
        }

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        caseId
        (commitments: ISuppressionCommitments)
        (ct: CancellationToken)
        =
        task {
            commitments.Admit()
            let mutable after = Guid.Empty
            let mutable count = 0L
            let mutable more = true

            while more do
                let! page = readPage connection transaction caseId after ct

                match List.tryLast page with
                | None -> more <- false
                | Some(last, _) ->
                    for operationId, priority in page do
                        do!
                            requireStored
                                connection
                                transaction
                                caseId
                                commitments
                                operationId
                                priority
                                ct

                        count <- count + 1L

                    after <- last

            use total =
                new NpgsqlCommand(
                    "SELECT count(*) FROM claimcore.case_erasure_operation_denials WHERE case_id=@case",
                    connection,
                    transaction
                )

            Sql.uuid total "case" caseId
            let! storedCount = total.ExecuteScalarAsync(ct)

            if unbox<int64> storedCount <> count then
                invalidOp "Erasure operation denial set contains an unaccounted identity."

            return count
        }
