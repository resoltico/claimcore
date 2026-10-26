namespace ClaimCore.Postgres

open ClaimCore.Contract
open ClaimCore.Domain

open System
open System.Net
open Npgsql
open ClaimCore.Application

/// Immutable installation configuration read by installation checks.
type Installation =
    {
        InstallationId: Guid
        BusinessTimeZone: string
    }

exception internal InstallationRefusal of RuntimeOpenFault

module internal InstallationCheck =
    let transport (settings: NpgsqlConnectionStringBuilder) =
        let loopback (host: string) =
            match IPAddress.TryParse host with
            | true, address -> IPAddress.IsLoopback(nonNull address)
            | _ -> String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)

        if (nonNull settings.Host).Split(',') |> Array.exists (loopback >> not) then
            if settings.SslMode <> SslMode.VerifyFull then
                raise (InstallationRefusal RuntimeOpenFault.InvalidTransport)

    let internal serverVersion value =
        if not (ServerSupport.supports value) then
            raise (InstallationRefusal RuntimeOpenFault.UnsupportedServer)

    let private role connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT r.rolsuper OR pg_has_role(r.oid,n.nspowner,'USAGE') OR pg_has_role(r.oid,n.nspowner,'SET') FROM pg_roles r CROSS JOIN pg_namespace n WHERE r.rolname=current_user AND n.nspname='claimcore'"
                    [||]
                    token

            if rows.IsEmpty then
                raise (InstallationRefusal RuntimeOpenFault.SchemaMismatch)

            if rows.Length <> 1 || DatabaseSession.value<bool> 0 rows.Head then
                raise (InstallationRefusal RuntimeOpenFault.UnsafeRole)
        }

    let private head connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT h.sequence,h.entry_hash,l.sequence,l.entry_hash FROM claimcore.ledger_head h LEFT JOIN LATERAL (SELECT sequence,entry_hash FROM claimcore.ledger ORDER BY sequence DESC LIMIT 1) l ON true"
                    [||]
                    token

            match rows with
            | [ row ] ->
                let last = DatabaseSession.optional<int64> 2 row |> Option.defaultValue 0L

                let hash =
                    DatabaseSession.optional<byte array> 3 row
                    |> Option.defaultValue (Array.zeroCreate 32)

                if
                    DatabaseSession.value<int64> 0 row <> last
                    || DatabaseSession.value<byte array> 1 row <> hash
                then
                    raise (InstallationRefusal RuntimeOpenFault.HeadMismatch)
            | _ -> raise (InstallationRefusal RuntimeOpenFault.HeadMismatch)
        }

    let openRuntime (source: NpgsqlDataSource) token =
        task {
            transport (NpgsqlConnectionStringBuilder source.ConnectionString)

            return!
                DatabaseSession.read source token (fun connection ->
                    task {
                        serverVersion connection.PostgreSqlVersion

                        do! role connection token
                        let! id, zone = Baseline.require connection token
                        do! head connection token

                        return
                            {
                                InstallationId = id
                                BusinessTimeZone = zone
                            }
                    })
        }
