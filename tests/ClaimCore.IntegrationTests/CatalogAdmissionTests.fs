module ClaimCore.IntegrationTests.CatalogAdmissionTests

open System.IO
open Npgsql
open Expecto
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures

let private requireMutationRefusal sql =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()
    use transaction = connection.BeginTransaction()

    try
        use mutation = new NpgsqlCommand(sql, connection, transaction)
        mutation.ExecuteNonQuery() |> ignore

        Expect.throwsT<InvalidDataException>
            (fun () -> RuntimeSchema.requireCompatible connection)
            "Every catalog mutation must be refused before a claimant-bearing operation"
    finally
        transaction.Rollback()

let private weakerCheck =
    testCase "[CC-DB-001] same-name weaker enforced check is refused" (fun () ->
        requireMutationRefusal (
            "ALTER TABLE claimcore.cases DROP CONSTRAINT claimed_money; "
            + "ALTER TABLE claimcore.cases ADD CONSTRAINT claimed_money CHECK (claimed_amount >= 0)"
        ))

let private otherDrift =
    testCase "[CC-DB-001] catalog drift refuses extra and altered objects" (fun () ->
        for mutation in
            [
                "ALTER TABLE claimcore.cases ALTER COLUMN status SET DEFAULT 'OPENED'"
                "ALTER TABLE claimcore.cases ALTER COLUMN claimant_name TYPE text COLLATE \"C\""
                "ALTER TABLE claimcore.case_changes ALTER COLUMN request_sha256 DROP NOT NULL"
                "GRANT DELETE ON claimcore.cases TO claimcore_app"
                "ALTER DEFAULT PRIVILEGES IN SCHEMA claimcore GRANT SELECT ON TABLES TO claimcore_app"
                "CREATE FUNCTION claimcore.catalog_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1'"
                "CREATE FUNCTION claimcore.catalog_probe() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END'; "
                + "CREATE TRIGGER catalog_probe BEFORE UPDATE ON claimcore.cases FOR EACH ROW EXECUTE FUNCTION claimcore.catalog_probe()"
                "ALTER TABLE claimcore.cases ENABLE ROW LEVEL SECURITY; "
                + "CREATE POLICY catalog_probe ON claimcore.cases USING (true)"
                "ALTER TABLE claimcore.case_changes DROP CONSTRAINT case_changes_case_id_case_reference_fkey; "
                + "ALTER TABLE claimcore.case_changes ADD CONSTRAINT case_changes_case_id_case_reference_fkey "
                + "FOREIGN KEY (case_id, case_reference) REFERENCES claimcore.cases(case_id, case_reference) NOT VALID"
                "SET LOCAL allow_system_table_mods = on; "
                + "UPDATE pg_catalog.pg_index SET indisvalid = false "
                + "WHERE indexrelid = 'claimcore.request_preparations_by_prepared_at'::regclass"
                "SET LOCAL allow_system_table_mods = on; "
                + "UPDATE pg_catalog.pg_index SET indisready = false "
                + "WHERE indexrelid = 'claimcore.request_preparations_by_prepared_at'::regclass"
                "DROP INDEX claimcore.request_preparations_by_prepared_at"
                "CREATE TABLE claimcore.catalog_probe (id integer)"
                "CREATE COLLATION claimcore.catalog_probe FROM \"C\""
            ] do
            requireMutationRefusal mutation)

let tests = testList "catalog admission" [ weakerCheck; otherDrift ]
