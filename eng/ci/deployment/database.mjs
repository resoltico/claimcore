import assert from "node:assert/strict";

/** @param {(args: string[], expected?: number) => string} compose */
function queries(compose) {
  /** @param {string} service @param {string} user @param {string} database @param {string} sql */
  const query = (service, user, database, sql) =>
    compose([
      "exec",
      "--no-TTY",
      service,
      "psql",
      "-XAt",
      "-v",
      "ON_ERROR_STOP=1",
      "-U",
      user,
      "-d",
      database,
      "-c",
      sql,
    ]).trim();
  const installation = () =>
    query(
      "primary",
      "claimcore_primary_owner",
      "claimcore",
      "SELECT installation_id::text || ':' || lineage_id::text FROM claimcore.installation_lineage",
    );
  const witness = () =>
    query(
      "witness",
      "claimcore_witness_owner",
      "claimcore_witness",
      "SELECT installation_id::text || ':' || lineage_id::text FROM claimcore_witness.installation",
    );
  return { query, installation, witness };
}

/** @param {(args: string[], expected?: number) => string} compose */
export function databaseChecks(compose) {
  const { query, installation, witness } = queries(compose);
  assert.equal(installation(), witness());
  /** @type {[string, string, string, string][]} */
  const roles = [
    ["primary", "claimcore_app", "claimcore", "claimcore"],
    ["witness", "claimcore_witness_writer", "claimcore_witness", "claimcore_witness"],
  ];
  for (const [service, user, database, schema] of roles) {
    assert.equal(
      query(
        service,
        user,
        database,
        "SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls FROM pg_roles WHERE rolname=current_user",
      ),
      "f",
    );
    compose(
      [
        "exec",
        "--no-TTY",
        service,
        "psql",
        "-X",
        "-v",
        "ON_ERROR_STOP=1",
        "-U",
        user,
        "-d",
        database,
        "-c",
        `CREATE TABLE ${schema}.operating_authority_canary (id integer)`,
      ],
      1,
    );
    assert.equal(
      query(
        service,
        user,
        database,
        `SELECT to_regclass('${schema}.operating_authority_canary') IS NULL`,
      ),
      "t",
    );
  }
  return installation;
}
