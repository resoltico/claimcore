/// Catalog writes as the owner, in the shape the admission checks are built to notice, grouped by
/// what they alter. The token tests apply each in a rolled-back transaction.
module ClaimCore.IntegrationTests.CatalogEpochChanges

open Npgsql
open ClaimCore.IntegrationTests.Fixtures

type Change = string * string list

let private cases = "claimcore.cases"
let private events = "claimcore.actor_authority_events"

let private columns: Change list =
    [
        "column rename", [ $"ALTER TABLE {events} RENAME COLUMN event_id TO event_id_x" ]
        "column add", [ $"ALTER TABLE {events} ADD COLUMN extra integer" ]
        "column drop", [ $"ALTER TABLE {events} DROP COLUMN event_id CASCADE" ]
        "column default", [ $"ALTER TABLE {cases} ALTER COLUMN claimant_name SET DEFAULT 'x'" ]
        "column not null", [ $"ALTER TABLE {cases} ALTER COLUMN claimant_name DROP NOT NULL" ]
        "column storage", [ $"ALTER TABLE {cases} ALTER COLUMN claimant_name SET STORAGE EXTERNAL" ]
        "column type", [ $"ALTER TABLE {cases} ALTER COLUMN claimant_name TYPE varchar(300)" ]
    ]

let private constraintsAndIndexes: Change list =
    [
        "constraint add",
        [
            $"ALTER TABLE {cases} ADD CONSTRAINT probe_check CHECK (revision < 99999999)"
        ]
        "constraint drop", [ $"ALTER TABLE {cases} DROP CONSTRAINT cases_status_check" ]
        "constraint not valid",
        [
            $"ALTER TABLE {cases} ADD CONSTRAINT probe_not_valid CHECK (revision > -1) NOT VALID"
        ]
        "constraint deferrable",
        [
            "ALTER TABLE claimcore.case_changes ALTER CONSTRAINT case_changes_case_id_case_reference_fkey DEFERRABLE"
        ]
        "index create", [ $"CREATE INDEX probe_index ON {cases} (claimant_name)" ]
        "index options",
        [
            "ALTER INDEX claimcore.actor_authority_events_event_id_key SET (fillfactor = 50)"
        ]
    ]

let private relations: Change list =
    [
        "table options", [ $"ALTER TABLE {cases} SET (fillfactor = 70)" ]
        "table unlogged", [ $"ALTER TABLE {events} SET UNLOGGED" ]
        "table replica identity", [ $"ALTER TABLE {events} REPLICA IDENTITY FULL" ]
        "row security", [ $"ALTER TABLE {cases} ENABLE ROW LEVEL SECURITY" ]
        "forced row security", [ $"ALTER TABLE {cases} FORCE ROW LEVEL SECURITY" ]
        "table create", [ "CREATE TABLE claimcore.probe_table (a integer)" ]
        "table drop", [ "DROP TABLE claimcore.request_preparation_prunes CASCADE" ]
        "table rename",
        [ "ALTER TABLE claimcore.request_preparation_prunes RENAME TO probe_renamed" ]
        "table inheritance", [ $"CREATE TABLE claimcore.probe_child () INHERITS ({cases})" ]
        "sequence alter",
        [
            "ALTER SEQUENCE claimcore.request_preparation_prunes_prune_id_seq INCREMENT BY 7"
        ]
        "sequence create", [ "CREATE SEQUENCE claimcore.probe_sequence" ]
        "view create", [ "CREATE VIEW claimcore.probe_view AS SELECT 1 AS a" ]
    ]

let private programObjects: Change list =
    [
        "function create",
        [
            "CREATE FUNCTION claimcore.probe_fn() RETURNS integer LANGUAGE sql AS 'SELECT 1'"
        ]
        "trigger create",
        [
            "CREATE FUNCTION claimcore.probe_trigger_fn() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END'"
            $"CREATE TRIGGER probe_trigger BEFORE INSERT ON {cases} FOR EACH ROW EXECUTE FUNCTION claimcore.probe_trigger_fn()"
        ]
        "rule create",
        [
            "CREATE TABLE claimcore.probe_ruled (a integer)"
            "CREATE RULE probe_rule AS ON INSERT TO claimcore.probe_ruled DO ALSO NOTHING"
        ]
        "enum create", [ "CREATE TYPE claimcore.probe_enum AS ENUM ('a')" ]
        "policy create", [ $"CREATE POLICY probe_policy ON {cases} USING (true)" ]
        "operator create",
        [
            "CREATE FUNCTION claimcore.probe_operator_fn(integer, integer) RETURNS boolean LANGUAGE sql AS 'SELECT true'"
            "CREATE OPERATOR claimcore.=== (LEFTARG = integer, RIGHTARG = integer, FUNCTION = claimcore.probe_operator_fn)"
        ]
        "operator family create", [ "CREATE OPERATOR FAMILY claimcore.probe_family USING btree" ]
        "extension create", [ "CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA claimcore" ]
    ]

let private textAndEncoding: Change list =
    [
        "collation create",
        [ "CREATE COLLATION claimcore.probe_collation (provider = libc, locale = 'C')" ]
        "conversion create",
        [
            "CREATE CONVERSION claimcore.probe_conversion FOR 'UTF8' TO 'LATIN1' FROM utf8_to_iso8859_1"
        ]
        "text search configuration create",
        [
            "CREATE TEXT SEARCH CONFIGURATION claimcore.probe_config (COPY = pg_catalog.simple)"
        ]
        "text search dictionary create",
        [
            "CREATE TEXT SEARCH DICTIONARY claimcore.probe_dictionary (TEMPLATE = pg_catalog.simple)"
        ]
    ]

let private privileges: Change list =
    [
        "table grant", [ $"GRANT SELECT ON {cases} TO claimcore_app" ]
        "table grant to everyone", [ $"GRANT SELECT ON {cases} TO PUBLIC" ]
        "column grant", [ $"GRANT UPDATE (claimant_name) ON {cases} TO claimcore_app" ]
        "schema grant", [ "GRANT CREATE ON SCHEMA claimcore TO claimcore_app" ]
        "database grant",
        [
            "DO $$ BEGIN EXECUTE format('GRANT TEMPORARY ON DATABASE %I TO claimcore_app', current_database()); END $$"
        ]
        "sequence grant",
        [
            "GRANT USAGE ON SEQUENCE claimcore.request_preparation_prunes_prune_id_seq TO claimcore_app"
        ]
        "function grant",
        [
            "CREATE FUNCTION claimcore.probe_granted_fn() RETURNS integer LANGUAGE sql AS 'SELECT 1'"
            "GRANT EXECUTE ON FUNCTION claimcore.probe_granted_fn() TO claimcore_app"
        ]
        "table revoke", [ $"REVOKE ALL ON {cases} FROM claimcore_app" ]
        "default privileges",
        [
            "ALTER DEFAULT PRIVILEGES IN SCHEMA claimcore GRANT SELECT ON TABLES TO claimcore_app"
        ]
    ]

let private ownersAndRoles () : Change list =
    let admin = NpgsqlConnectionStringBuilder(adminConnection ()).Username

    [
        "table owner", [ $"ALTER TABLE {cases} OWNER TO claimcore_app" ]
        "schema owner", [ "ALTER SCHEMA claimcore OWNER TO claimcore_app" ]
        "role rename", [ "ALTER ROLE claimcore_app RENAME TO claimcore_app_renamed" ]
        "role attribute", [ "ALTER ROLE claimcore_app CREATEDB" ]
        "role create", [ "CREATE ROLE claimcore_probe_role" ]
        "role membership", [ $"GRANT \"{admin}\" TO claimcore_app" ]
        "database owner",
        [
            "DO $$ BEGIN EXECUTE format('ALTER DATABASE %I OWNER TO claimcore_app', current_database()); END $$"
        ]
    ]

/// Every change the primary admission checks could notice.
let primary () : Change list =
    List.concat
        [
            columns
            constraintsAndIndexes
            relations
            programObjects
            textAndEncoding
            privileges
            ownersAndRoles ()
        ]

/// Activity outside what the checks read; it must not disturb the token.
let neutral: Change list =
    [
        "comment", [ "COMMENT ON TABLE claimcore.cases IS 'probe'" ]
        "statistics", [ "ANALYZE claimcore.cases" ]
        "temporary table", [ "CREATE TEMP TABLE probe_temporary (a integer)" ]
        "unrelated schema",
        [
            "CREATE SCHEMA probe_unrelated"
            "CREATE TABLE probe_unrelated.t (a integer)"
        ]
    ]

/// Every change the witness admission checks could notice, as its schema owner.
let witness: Change list =
    [
        "table create", [ "CREATE TABLE claimcore_witness.probe_table (a integer)" ]
        "column add", [ "ALTER TABLE claimcore_witness.journal ADD COLUMN extra integer" ]
        "table grant", [ "GRANT UPDATE ON claimcore_witness.journal TO claimcore_witness_writer" ]
        "table revoke",
        [ "REVOKE SELECT ON claimcore_witness.journal FROM claimcore_witness_auditor" ]
        "function create",
        [
            "CREATE FUNCTION claimcore_witness.probe_fn() RETURNS integer LANGUAGE sql AS 'SELECT 1'"
        ]
        "policy create", [ "CREATE POLICY probe_policy ON claimcore_witness.journal USING (true)" ]
        "default privileges",
        [
            "ALTER DEFAULT PRIVILEGES IN SCHEMA claimcore_witness GRANT SELECT ON TABLES TO claimcore_witness_writer"
        ]
        "role attribute", [ "ALTER ROLE claimcore_witness_writer CREATEDB" ]
        "schema owner", [ "ALTER SCHEMA claimcore_witness OWNER TO claimcore_witness_writer" ]
    ]
