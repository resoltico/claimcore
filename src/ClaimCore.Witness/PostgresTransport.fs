namespace ClaimCore.Witness

open System
open Npgsql

/// A private connection file does not authenticate a remote PostgreSQL peer.
module PostgresTransport =
    let private localHost (host: string) =
        String.Equals(host, "127.0.0.1", StringComparison.Ordinal)
        || String.Equals(host, "::1", StringComparison.Ordinal)
        || String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)

    let requireAuthenticatedRemote (builder: NpgsqlConnectionStringBuilder) =
        let host = builder.Host |> Option.ofObj |> Option.defaultValue ""

        if localHost host then
            true
        elif builder.SslMode <> SslMode.VerifyFull then
            false
        else
            // GSS encryption can be selected before TLS. Require the verified TLS path.
            builder.GssEncryptionMode <- GssEncryptionMode.Disable
            builder.CheckCertificateRevocation <- true
            true

    /// All admitted PostgreSQL waits include finite cancellation readback.
    let applyBudgets commandSeconds (builder: NpgsqlConnectionStringBuilder) =
        if commandSeconds < 1 then
            invalidArg (nameof commandSeconds) "PostgreSQL command budget must be finite."

        builder.Timeout <- 5
        builder.CommandTimeout <- commandSeconds
        builder.CancellationTimeout <- 2000

    let connectionString (raw: string) =
        let builder = NpgsqlConnectionStringBuilder(raw)

        if not (requireAuthenticatedRemote builder) then
            invalidArg (nameof raw) "Remote PostgreSQL transport is not authenticated."

        applyBudgets 30 builder
        builder.ConnectionString

    let connection raw =
        new NpgsqlConnection(connectionString raw)
