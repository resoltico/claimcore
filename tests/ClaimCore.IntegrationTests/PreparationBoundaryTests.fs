module ClaimCore.IntegrationTests.PreparationBoundaryTests

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.IntegrationTests.Fixtures

let private draft () =
    let request = newRequest ()
    let canonical = RequestRecord.encode request

    {
        OperationId = request.OperationId
        CanonicalRequestFormat = RecordVersions.CanonicalCommandFormat
        RequestSha256 = canonical |> SHA256.HashData |> Convert.ToHexStringLower
        CanonicalRequest = canonical
        PreparingApplicationVersion = BuildIdentity.current.Version
        PreparingContractFingerprint =
            SemanticContract.fingerprint SemanticContract.current
            |> SemanticCoreFingerprint.value
        PreparingContractKind = PreparingContractKind.SemanticCoreV1
    }

let private currentPendingPreparationCount () =
    use connection = new NpgsqlConnection(appConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.request_preparations p "
            + "WHERE NOT EXISTS (SELECT 1 FROM claimcore.case_changes c WHERE c.operation_id = p.operation_id) "
            + "AND NOT EXISTS (SELECT 1 FROM claimcore.operation_revocations r WHERE r.operation_id = p.operation_id)",
            connection
        )

    command.ExecuteScalar() :?> int64

let private capacityRefusal =
    testCase "capacity refusal preserves retained material and permits exact replay" (fun () ->
        use source = NpgsqlDataSource.Create(appConnection ())

        let limits =
            { PreparationLimits.defaults with
                MaximumPreparations = int (currentPendingPreparationCount ()) + 1
            }

        let recovery = PostgresRecoveryStore(source, limits) :> IRecoveryStore
        let first = draft ()
        let second = draft ()

        match recovery.Retain(first, CancellationToken.None) |> await with
        | Ok(RecoveryRetain.Created retained) ->
            Expect.equal retained.CanonicalRequest first.CanonicalRequest "Exact retained bytes"
        | _ -> failtest "The first preparation must fit the final available slot."

        match recovery.Retain(second, CancellationToken.None) |> await with
        | Error RecoveryStoreFailure.CapacityExceeded -> ()
        | _ -> failtest "A distinct preparation must be refused at the configured capacity."

        match recovery.Get(first.OperationId, CancellationToken.None) |> await with
        | Ok(Some(RecoveryStoredOperation.Retained(retained, _))) ->
            Expect.equal retained.RequestSha256 first.RequestSha256 "Refusal cannot erase recovery"
        | _ -> failtest "The retained first preparation must remain readable."

        match recovery.Retain(first, CancellationToken.None) |> await with
        | Ok(RecoveryRetain.Existing retained) ->
            Expect.equal retained.CanonicalRequest first.CanonicalRequest "Exact replay"
        | _ -> failtest "An exact retained replay must succeed even at capacity.")

let private atomicRetainClassification =
    testCase
        "[CC-REC-001] concurrent same-ID retain reports one creator and one existing replay"
        (fun () ->
            use source = NpgsqlDataSource.Create(appConnection ())

            let recovery =
                PostgresRecoveryStore(source, PreparationLimits.defaults) :> IRecoveryStore

            let material = draft ()

            let results =
                [|
                    recovery.Retain(material, CancellationToken.None)
                    recovery.Retain(material, CancellationToken.None)
                |]
                |> System.Threading.Tasks.Task.WhenAll
                |> await

            let created =
                results
                |> Array.filter (function
                    | Ok(RecoveryRetain.Created _) -> true
                    | _ -> false)

            let existing =
                results
                |> Array.filter (function
                    | Ok(RecoveryRetain.Existing _) -> true
                    | _ -> false)

            Expect.equal created.Length 1 "One transaction created the retained row"
            Expect.equal existing.Length 1 "The other observed the exact existing row")

let private stableInstallationLineage =
    testCase "installation lineage remains stable and non-empty" (fun () ->
        use source = NpgsqlDataSource.Create(appConnection ())

        let recovery =
            PostgresRecoveryStore(source, PreparationLimits.defaults) :> IRecoveryStore

        let read () =
            recovery.InstallationLineage CancellationToken.None
            |> await
            |> Result.defaultWith (fun _ -> failtest "Installation lineage must be readable.")

        let first = read ()
        Expect.notEqual first Guid.Empty "Lineage cannot be the empty UUID"
        Expect.equal (read ()) first "Lineage is stable across independent reads")

let private startedPreparation () =
    use runtime =
        Runtime.OpenPostgres(appConnection (), CancellationToken.None)
        |> await
        |> Result.defaultWith (fun _ -> failtest "Runtime must open for lifecycle qualification.")

    let operationId = Guid.NewGuid()

    let digest =
        match
            runtime.Core.Prepare(
                openRequest operationId ("BOUNDARY-" + Guid.NewGuid().ToString("N")),
                CancellationToken.None
            )
            |> await
        with
        | PrepareOutcome.Prepared(details, _) ->
            details.Summary.RequestSha256
            |> Option.defaultWith (fun () -> failtest "Preparation digest is required.")
        | _ -> failtest "A fresh preparation must be retained."

    use source = NpgsqlDataSource.Create(appConnection ())

    let recovery =
        PostgresRecoveryStore(source, PreparationLimits.defaults) :> IRecoveryStore

    match recovery.Start(operationId, CancellationToken.None) |> await with
    | Ok(RecoveryStart.Started _) -> operationId
    | _ -> failtestf "A retained preparation (%s) must create one lifecycle start marker." digest

let private expectSqlState expected sql operationId =
    use connection = new NpgsqlConnection(appConnection ())
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)
    Sql.uuid command "operation" operationId

    let actual =
        try
            command.ExecuteNonQuery() |> ignore
            None
        with :? PostgresException as error ->
            Some error.SqlState

    Expect.equal actual (Some expected) "The runtime role must reject lifecycle mutation"

let private appendOnlyLifecycle =
    testCase "lifecycle markers are append-only and mutually exclusive" (fun () ->
        let operationId = startedPreparation ()

        expectSqlState
            "23505"
            "INSERT INTO claimcore.request_preparation_lifecycle (operation_id, state) VALUES (@operation, 'SUBMISSION_STARTED')"
            operationId

        expectSqlState
            "42501"
            "UPDATE claimcore.request_preparation_lifecycle SET state = 'SUBMISSION_STARTED' WHERE operation_id = @operation"
            operationId

        expectSqlState
            "42501"
            "DELETE FROM claimcore.request_preparation_lifecycle WHERE operation_id = @operation"
            operationId)

let private executeOwner sql =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let private addedGrant =
    testCase "runtime startup rejects an added preparation-object grant" (fun () ->
        executeOwner "GRANT UPDATE ON claimcore.request_preparations TO claimcore_app"

        try
            match Runtime.OpenPostgres(appConnection (), CancellationToken.None) |> await with
            | Error RuntimeOpenFault.RuntimeSchemaMismatch -> ()
            | Error _ -> failtest "Expanded recovery ACL must be classified as schema mismatch."
            | Ok runtime ->
                use _ = runtime
                failtest "An expanded recovery ACL must not open a runtime."
        finally
            executeOwner "REVOKE UPDATE ON claimcore.request_preparations FROM claimcore_app"

        use restored =
            Runtime.OpenPostgres(appConnection (), CancellationToken.None)
            |> await
            |> Result.defaultWith (fun _ ->
                failtest "The exact ACL must remain usable after restore.")

        restored.Core.Describe() |> ignore)

let private ownerOnlyPruneAudit =
    testCase "runtime cannot inspect the owner-only preparation prune audit" (fun () ->
        use connection = new NpgsqlConnection(appConnection ())
        connection.Open()

        use command =
            new NpgsqlCommand(
                "SELECT count(*) FROM claimcore.request_preparation_prunes",
                connection
            )

        let sqlState =
            try
                command.ExecuteScalar() |> ignore
                None
            with :? PostgresException as error ->
                Some error.SqlState

        Expect.equal sqlState (Some "42501") "Prune audit belongs to the schema owner")

let tests =
    testList
        "PostgreSQL preparation boundary"
        [
            capacityRefusal
            atomicRetainClassification
            stableInstallationLineage
            appendOnlyLifecycle
            addedGrant
            ownerOnlyPruneAudit
        ]
