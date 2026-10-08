import assert from "node:assert/strict";
import test from "node:test";
import { nativeSql } from "./sql-native.mjs";
import { sqlFindings } from "./sql.mjs";
import { findings } from "./test-support.mjs";

const limits = { lines: 50, complexity: 12, parameters: 5 };
/** @param {string} source */
function inspect(source) {
  return sqlFindings(source, nativeSql({ "probe.sql": source })["probe.sql"], limits);
}
/** @param {string} body @param {string} [parameters] */
function procedure(body, parameters = "") {
  return `CREATE FUNCTION public.probe(${parameters}) RETURNS void LANGUAGE plpgsql AS $body$\n${body}\n$body$;`;
}

test("native SQL controls admit actual boundaries and refuse whole function physical spans", () => {
  const base = procedure(`BEGIN\n${"NULL;\n".repeat(46)}END;`);
  assert.deepEqual(inspect(base), [], "The complete CREATE statement has fifty physical lines.");
  assert.match(inspect(base.replace("END;", "\nEND;")).join(), /51 function lines/u);
  assert.deepEqual(inspect(`-- Unicode α and CREATE decoy\n${procedure("BEGIN END;")}`), []);
  assert.deepEqual(findings({ "db/probe.sql": "\n".repeat(300) }, []), []);
  assert.match(findings({ "db/probe.sql": "\n".repeat(301) }, []).join(), /301 physical lines/u);
});

test("native SQL counts input, inout and variadic parameters while excluding result columns", () => {
  assert.deepEqual(inspect(procedure("BEGIN END;", "a int,b int,c int,d int,e int")), []);
  assert.match(
    inspect(procedure("BEGIN END;", "a int,b int,c int,d int,e int,f int")).join(),
    /6 input parameters/u,
  );
  const source =
    "CREATE PROCEDURE public.probe(INOUT a int,OUT b int) LANGUAGE plpgsql AS $$BEGIN END;$$;";
  assert.deepEqual(inspect(source), []);
  assert.deepEqual(
    inspect(
      "CREATE FUNCTION public.probe(a int,VARIADIC c int[]) RETURNS int LANGUAGE sql AS 'SELECT 1';",
    ),
    [],
  );
});

test("minification, quoted bodies and comment/string decoys cannot bypass control complexity", () => {
  const branches = "IF TRUE THEN NULL; END IF;".repeat(12);
  assert.match(inspect(procedure(`BEGIN ${branches} END;`)).join(), /13 control decisions/u);
  const quoted = `CREATE FUNCTION public.probe() RETURNS void AS 'BEGIN ${branches} END;' LANGUAGE plpgsql;`;
  assert.match(inspect(quoted).join(), /13 control decisions/u);
  assert.deepEqual(inspect(procedure("BEGIN PERFORM 'IF THEN CASE WHEN LOOP'; /* IF */ END;")), []);
});

test("native nested SQL CASE expressions and SQL language bodies are measured", () => {
  const arms = Array.from({ length: 12 }, () => "WHEN TRUE THEN 1").join(" ");
  assert.match(
    inspect(procedure(`BEGIN PERFORM CASE ${arms} ELSE 0 END; END;`)).join(),
    /13 control decisions/u,
  );
  const sql = `CREATE FUNCTION public.probe() RETURNS int LANGUAGE sql AS $$SELECT CASE ${arms} ELSE 0 END$$;`;
  assert.match(inspect(sql).join(), /13 control decisions/u);
  const atomic = `CREATE FUNCTION public.probe() RETURNS int LANGUAGE sql BEGIN ATOMIC SELECT CASE ${arms} ELSE 0 END; END;`;
  assert.match(inspect(atomic).join(), /13 control decisions/u);
});

test("native DO, loops, elsif, cases and exception handlers cannot escape the inventory", () => {
  const branches = "IF TRUE THEN NULL; ELSIF FALSE THEN NULL; END IF;".repeat(6);
  assert.match(inspect(`DO $$BEGIN ${branches} END;$$;`).join(), /13 control decisions/u);
  const loops = "FOR i IN 1..1 LOOP NULL; END LOOP;".repeat(12);
  assert.match(
    inspect(procedure(`DECLARE i int; BEGIN ${loops} END;`)).join(),
    /13 control decisions/u,
  );
  const guarded = "BEGIN NULL; EXCEPTION WHEN OTHERS THEN NULL; END;".repeat(12);
  assert.match(inspect(procedure(`BEGIN ${guarded} END;`)).join(), /13 control decisions/u);
});

test("custom typed headers retain exact body syntax while real database tests own semantic binding", () => {
  const source =
    "CREATE FUNCTION authority.probe(p authority.proof) RETURNS authority.receipt LANGUAGE plpgsql AS $$BEGIN IF p.id IS NULL THEN RAISE EXCEPTION 'refuse'; END IF; RETURN p; END$$;";
  assert.deepEqual(inspect(source), []);
  const broken = source.replace("RETURN p;", "RETURN (;");
  assert.throws(() => inspect(broken), /Native PostgreSQL syntax parsing was refused/u);
});

test("unsupported languages, dynamic execution and nested function construction fail closed", () => {
  assert.throws(
    () => inspect("CREATE FUNCTION public.probe() RETURNS void LANGUAGE plpython3u AS $$pass$$;"),
    /Unsupported/u,
  );
  assert.throws(() => inspect(procedure("BEGIN EXECUTE 'SELECT 1'; END;")), /Unsupported/u);
  const ddl = "CREATE FUNCTION public.other() RETURNS int LANGUAGE sql AS 'SELECT 1';";
  assert.throws(() => inspect(procedure(`BEGIN ${ddl} END;`)), /Nested executable SQL/u);
});

test("missing native bodies, invalid parser output and unknown procedural node shapes refuse", () => {
  const source = procedure("BEGIN END;");
  const parsed = nativeSql({ "probe.sql": source })["probe.sql"];
  assert.ok(parsed && typeof parsed === "object");
  const tree = /** @type {{bodies:unknown[]}} */ (parsed);
  tree.bodies = [];
  assert.throws(() => sqlFindings(source, tree, limits), /inventory differs/u);
  assert.throws(() => sqlFindings(source, {}, limits), /shape was refused/u);
});
