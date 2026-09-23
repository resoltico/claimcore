namespace ClaimCore.Postgres

open System
open Npgsql
open ClaimCore.Application

/// Schema administration is separate from case-work admission and cannot use the runtime role.
module internal OwnerConnection =
    let builder (connectionString: string) =
        let builder = NpgsqlConnectionStringBuilder(connectionString)

        if
            String.IsNullOrWhiteSpace(builder.Username)
            || builder.Username = "claimcore_app"
            || not (String.IsNullOrWhiteSpace(builder.Options))
            || builder.NoResetOnClose
            || builder.LogParameters
            || builder.PersistSecurityInfo
            || not (ConnectionTransport.requireAuthenticatedRemote builder)
        then
            AdministrationFailures.refuse AdministrationFailure.OwnerConnectionInvalid

        builder.Enlist <- false
        builder.Timeout <- 5
        builder.CommandTimeout <- 30
        builder.IncludeErrorDetail <- false
        builder.LogParameters <- false
        builder.PersistSecurityInfo <- false
        builder.NoResetOnClose <- false

        builder.ApplicationName <-
            BuildIdentity.current.Product
            + "/"
            + BuildIdentity.current.Version
            + " database"

        builder

    let requireIdentity (connection: NpgsqlConnection) =
        use identity =
            new NpgsqlCommand(
                "SELECT session_user = current_user AND current_user <> 'claimcore_app' "
                + "AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace n "
                + "WHERE n.nspname = 'claimcore' AND n.nspowner <> "
                + "(SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user))",
                connection
            )

        if not (identity.ExecuteScalar() :?> bool) then
            AdministrationFailures.refuse AdministrationFailure.OwnerIdentityRejected
