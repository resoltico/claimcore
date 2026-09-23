namespace ClaimCore.Postgres

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Application

/// Verified on every checkout; this validates infrastructure admission, not authentication.
module internal RuntimeDatabase =
    let requireCompatible (connection: NpgsqlConnection) =
        try
            RuntimeAcl.requireRole connection
            RuntimeSchema.requireCompatible connection
            RuntimeAcl.requireAcl connection
        with
        | :? PostgresException as error when
            error.SqlState = "42P01"
            || error.SqlState = "3F000"
            || error.SqlState = "42501"
            || error.SqlState = "42703"
            ->
            raise RuntimeDatabaseMismatch
        | :? InvalidCastException
        | :? IndexOutOfRangeException
        | :? InvalidDataException -> raise RuntimeDatabaseMismatch

    let requireCompatibleAsyncWithCancellation
        (connection: NpgsqlConnection)
        (cancellationToken: CancellationToken)
        =
        task {
            try
                do! RuntimeAcl.requireRoleAsyncWithCancellation connection cancellationToken

                do!
                    RuntimeSchema.requireCompatibleAsyncWithCancellation
                        connection
                        cancellationToken

                do! RuntimeAcl.requireAclAsyncWithCancellation connection cancellationToken
            with
            | :? PostgresException as error when
                error.SqlState = "42P01"
                || error.SqlState = "3F000"
                || error.SqlState = "42501"
                || error.SqlState = "42703"
                ->
                return raise RuntimeDatabaseMismatch
            | :? InvalidCastException
            | :? IndexOutOfRangeException
            | :? InvalidDataException -> return raise RuntimeDatabaseMismatch
        }

    let requireCompatibleAsync (connection: NpgsqlConnection) =
        requireCompatibleAsyncWithCancellation connection CancellationToken.None

    let openConnection (dataSource: NpgsqlDataSource) =
        let connection = dataSource.OpenConnection()

        try
            DatabaseEnvironment.requireCompatible connection
            requireCompatible connection
            connection
        with _ ->
            connection.Dispose()
            reraise ()

    let openConnectionAsyncWithCancellation
        (dataSource: NpgsqlDataSource)
        (cancellationToken: CancellationToken)
        =
        task {
            let! connection = dataSource.OpenConnectionAsync(cancellationToken)

            try
                do!
                    DatabaseEnvironment.requireCompatibleAsyncWithCancellation
                        connection
                        cancellationToken

                do! requireCompatibleAsyncWithCancellation connection cancellationToken
                return connection
            with error ->
                connection.Dispose()
                return raise error
        }

    let openConnectionAsync (dataSource: NpgsqlDataSource) =
        openConnectionAsyncWithCancellation dataSource CancellationToken.None

/// One configured data source is created by Runtime and borrowed by its private stores.  Test-only
/// direct adapter construction may still own an isolated source, but product Runtime never creates
/// a second pool for recovery bookkeeping.
module internal RuntimeDataSource =
    let create (connectionString: string) =
        let builder = NpgsqlConnectionStringBuilder(connectionString)

        if
            builder.Username <> "claimcore_app"
            || not (String.IsNullOrWhiteSpace(builder.Options))
            || builder.NoResetOnClose
            || builder.LogParameters
            || builder.PersistSecurityInfo
            || not (ConnectionTransport.requireAuthenticatedRemote builder)
        then
            invalidArg
                (nameof connectionString)
                "Use an explicit claimcore_app login without startup options, parameter logging, retained secrets, or pool reset bypasses."

        builder.Enlist <- false
        builder.IncludeErrorDetail <- false
        builder.LogParameters <- false
        builder.PersistSecurityInfo <- false
        builder.NoResetOnClose <- false
        builder.Timeout <- 5
        builder.CommandTimeout <- 10

        builder.ApplicationName <-
            BuildIdentity.current.Product + "/" + BuildIdentity.current.Version

        NpgsqlDataSource.Create(builder.ConnectionString)
