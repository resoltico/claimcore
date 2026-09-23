module ClaimCore.IntegrationTests.FreshBaselineSupport

open System
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures

let execute connectionString sql =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let scalar connectionString sql =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)

    command.ExecuteScalar()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "Synthetic scalar is missing.")

let private quoteIdentifier value =
    use builder = new NpgsqlCommandBuilder()
    builder.QuoteIdentifier(value)

let private connectionFor database (source: string) =
    let builder = NpgsqlConnectionStringBuilder(source)
    builder.Database <- database
    builder.ConnectionString

// The fixture creates and owns this random isolated database; no developer volume is touched.
let withDatabase action =
    let database = "baseline_" + Guid.NewGuid().ToString("N") + "_test"
    let root = adminConnection ()
    let admin = connectionFor database root
    let app = connectionFor database (appConnection ())
    let quoted = quoteIdentifier database
    execute root ("CREATE DATABASE " + quoted)

    try
        execute
            root
            ("REVOKE ALL ON DATABASE "
             + quoted
             + " FROM PUBLIC; GRANT CONNECT ON DATABASE "
             + quoted
             + " TO claimcore_app")

        execute admin "REVOKE CREATE ON SCHEMA public FROM PUBLIC"
        action admin app
    finally
        NpgsqlConnection.ClearAllPools()
        execute root ("DROP DATABASE " + quoted)

let initialize admin =
    SchemaBaseline.initialize admin "Etc/UTC" |> completedAdministration

let runtimeRefuses (app: string) =
    use database = new PostgresStore(app)

    Expect.equal
        (database.CheckSchema() |> await)
        (Error CoreFailure.SchemaMismatch)
        "The runtime refuses unsupported storage before serving case work"

let private tableNames connectionString =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT c.relname FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace "
            + "WHERE n.nspname='claimcore' AND c.relkind='r' ORDER BY c.relname",
            connection
        )

    use reader = command.ExecuteReader()

    [
        while reader.Read() do
            reader.GetString(0)
    ]

let private catalogSql =
    """
        SELECT coalesce(jsonb_agg(jsonb_build_array(c.relname,c.relkind,c.relowner,c.relacl::text,
            (SELECT jsonb_agg(jsonb_build_array(a.attname,a.atttypid,a.attnotnull) ORDER BY a.attnum)
             FROM pg_catalog.pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped),
            (SELECT jsonb_agg(pg_get_constraintdef(k.oid) ORDER BY k.conname)
             FROM pg_catalog.pg_constraint k WHERE k.conrelid=c.oid)) ORDER BY c.relname),'[]'::jsonb)::text
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='claimcore'
            """

let snapshot admin =
    let catalog = scalar admin catalogSql :?> string

    let rows =
        tableNames admin
        |> List.map (fun name ->
            let sql =
                "SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb)::text FROM claimcore."
                + quoteIdentifier name
                + " r"

            name, scalar admin sql :?> string)

    catalog, rows

let assertUnsupported admin app =
    let before = snapshot admin

    SchemaBaseline.initialize admin "Etc/UTC"
    |> refusedAdministration AdministrationFailure.UnsupportedInstallation

    SchemaBaseline.verify admin
    |> refusedAdministration AdministrationFailure.UnsupportedInstallation

    PreparationPruning.prune admin PreparationPruneOptions.defaults
    |> refusedAdministration AdministrationFailure.UnsupportedInstallation

    runtimeRefuses app
    Expect.equal (snapshot admin) before "Refusal changes neither DDL, grants nor existing rows"
