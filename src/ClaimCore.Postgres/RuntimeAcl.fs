namespace ClaimCore.Postgres

open System
open System.Threading
open System.Threading.Tasks
open Npgsql

module internal RuntimeAcl =
    let private expected =
        let databaseAndSchema = [ true; false; true; false ]
        let tables = List.replicate 9 [ true; false ] |> List.concat
        databaseAndSchema @ tables @ [ false; false ]

    let requireRole (connection: NpgsqlConnection) =
        use command = new NpgsqlCommand(RuntimeAccessPolicy.roleSql, connection)
        use reader = command.ExecuteReader()

        if
            not (reader.Read())
            || reader.GetString(0) <> "claimcore_app"
            || reader.GetString(1) <> "claimcore_app"
            || reader.GetBoolean(2)
        then
            raise RuntimeDatabaseMismatch

    let requireRoleAsyncWithCancellation
        (connection: NpgsqlConnection)
        (cancellationToken: CancellationToken)
        =
        task {
            use command = new NpgsqlCommand(RuntimeAccessPolicy.roleSql, connection)
            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let! hasRow = reader.ReadAsync(cancellationToken)

            if
                not hasRow
                || reader.GetString(0) <> "claimcore_app"
                || reader.GetString(1) <> "claimcore_app"
                || reader.GetBoolean(2)
            then
                return raise RuntimeDatabaseMismatch
        }

    let requireRoleAsync (connection: NpgsqlConnection) =
        requireRoleAsyncWithCancellation connection CancellationToken.None

    let requireAcl (connection: NpgsqlConnection) =
        use command = new NpgsqlCommand(RuntimeAccessPolicy.aclSql, connection)
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            raise RuntimeDatabaseMismatch

        let actual = [ for index in 0 .. expected.Length - 1 -> reader.GetBoolean(index) ]

        if actual <> expected then
            raise RuntimeDatabaseMismatch

    let requireAclAsyncWithCancellation
        (connection: NpgsqlConnection)
        (cancellationToken: CancellationToken)
        =
        task {
            use command = new NpgsqlCommand(RuntimeAccessPolicy.aclSql, connection)
            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let! hasRow = reader.ReadAsync(cancellationToken)

            if not hasRow then
                return raise RuntimeDatabaseMismatch

            let actual = [ for index in 0 .. expected.Length - 1 -> reader.GetBoolean(index) ]

            if actual <> expected then
                return raise RuntimeDatabaseMismatch
        }

    let requireAclAsync (connection: NpgsqlConnection) =
        requireAclAsyncWithCancellation connection CancellationToken.None
