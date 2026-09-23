namespace ClaimCore.Postgres

open System
open Npgsql

/// A private connection file is not evidence that a remote PostgreSQL peer is authentic.
module internal ConnectionTransport =
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
            true
