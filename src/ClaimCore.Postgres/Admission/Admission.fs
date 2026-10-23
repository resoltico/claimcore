namespace ClaimCore.Postgres

open ClaimCore.Contract

open System
open System.Net
open Npgsql
open ClaimCore.Application

/// Immutable installation configuration read by runtime admission.
type Installation =
    {
        InstallationId: Guid
        BusinessTimeZone: string
    }

exception internal AdmissionRefusal of RuntimeOpenFault

module internal Admission =
    let transport (settings: NpgsqlConnectionStringBuilder) =
        let loopback (host: string) =
            match IPAddress.TryParse host with
            | true, address -> IPAddress.IsLoopback(nonNull address)
            | _ -> String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)

        if (nonNull settings.Host).Split(',') |> Array.exists (loopback >> not) then
            if settings.SslMode <> SslMode.VerifyFull then
                raise (AdmissionRefusal RuntimeOpenFault.InvalidTransport)

    let internal serverVersion value =
        if not (ServerSupport.supports value) then
            raise (AdmissionRefusal RuntimeOpenFault.UnsupportedServer)

    let private role connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT r.rolsuper OR pg_has_role(r.oid,n.nspowner,'USAGE') OR pg_has_role(r.oid,n.nspowner,'SET') FROM pg_roles r CROSS JOIN pg_namespace n WHERE r.rolname=current_user AND n.nspname='claimcore'"
                    [||]
                    token

            if rows.IsEmpty then
                raise (AdmissionRefusal RuntimeOpenFault.SchemaMismatch)

            if rows.Length <> 1 || DatabaseSession.value<bool> 0 rows.Head then
                raise (AdmissionRefusal RuntimeOpenFault.UnsafeRole)
        }

    let private validateInstallation (id, zone) =
        try
            Migrations.zone zone |> ignore

            if id = Guid.Empty then
                raise (AdmissionRefusal RuntimeOpenFault.InstallationInvalid)

            {
                InstallationId = id
                BusinessTimeZone = zone
            }
        with :? ArgumentException ->
            raise (AdmissionRefusal RuntimeOpenFault.InstallationInvalid)

    let private installation connection token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT installation_id,business_time_zone FROM claimcore.installation"
                    [||]
                    token

            match rows with
            | [ row ] ->
                return
                    validateInstallation (
                        DatabaseSession.value<Guid> 0 row,
                        DatabaseSession.value<string> 1 row
                    )
            | _ -> return raise (AdmissionRefusal RuntimeOpenFault.InstallationInvalid)
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
                    raise (AdmissionRefusal RuntimeOpenFault.HeadMismatch)
            | _ -> raise (AdmissionRefusal RuntimeOpenFault.HeadMismatch)
        }

    let openRuntime (source: NpgsqlDataSource) token =
        task {
            transport (NpgsqlConnectionStringBuilder source.ConnectionString)

            return!
                DatabaseSession.read source token (fun connection ->
                    task {
                        serverVersion connection.PostgreSqlVersion

                        do! role connection token
                        let! count = Migrations.history connection Migrations.scripts token

                        if count <> Migrations.scripts.Length then
                            raise (AdmissionRefusal RuntimeOpenFault.SchemaMismatch)

                        let! installation = installation connection token
                        do! head connection token
                        return installation
                    })
        }
