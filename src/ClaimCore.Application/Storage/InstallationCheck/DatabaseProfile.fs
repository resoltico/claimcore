namespace ClaimCore.Application.Storage

open ClaimCore.Application.Contracts
open Npgsql

module internal DatabaseProfile =
    let require (connection: NpgsqlConnection) token =
        task {
            // Report an unsupported server before accessing catalogs that vary by major.
            ServerSupport.versionFault ServerSupport.supported connection.PostgreSqlVersion
            |> Option.iter (OperatorFaultException >> raise)

            let! rows =
                DatabaseSession.rows
                    connection
                    """
                    SELECT pg_encoding_to_char(encoding), datlocprovider::text, coalesce(datlocale,''), current_setting('data_checksums')
                    FROM pg_database
                    WHERE datname = current_database()
                    """
                    [||]
                    token

            match rows with
            | [ row ] ->
                {
                    Encoding = DatabaseSession.value 0 row
                    Provider = DatabaseSession.value 1 row
                    Locale = DatabaseSession.value 2 row
                    Checksums = DatabaseSession.value 3 row
                }
                |> ServerSupport.profileFault
                |> Option.iter (OperatorFaultException >> raise)
            | _ -> return invalidOp "PostgreSQL did not report one current database profile."
        }
