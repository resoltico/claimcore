namespace ClaimCore.Application.Storage

open ClaimCore.Application.Contracts

module internal OwnerAdmission =
    let require connection token =
        task {
            do! DatabaseProfile.require connection token

            let! rows =
                DatabaseSession.rows
                    connection
                    """
                    SELECT r.oid = d.datdba AND NOT (r.rolsuper OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls)
                    FROM pg_catalog.pg_roles r CROSS JOIN pg_catalog.pg_database d
                    WHERE r.rolname = session_user AND r.rolname = current_user
                      AND d.datname = pg_catalog.current_database()
                    """
                    [||]
                    token

            match rows with
            | [ row ] when DatabaseSession.value<bool> 0 row -> ()
            | _ -> raise (OperatorFaultException OperatorFault.PermissionDenied)
        }
