/** @typedef {{lines:number, complexity:number, parameters:number}} SqlLimits */

const decisions = new Set([
  "PLpgSQL_stmt_if",
  "PLpgSQL_if_elsif",
  "PLpgSQL_stmt_loop",
  "PLpgSQL_stmt_while",
  "PLpgSQL_stmt_fori",
  "PLpgSQL_stmt_fors",
  "PLpgSQL_stmt_forc",
  "PLpgSQL_stmt_foreach_a",
  "PLpgSQL_case_when",
  "PLpgSQL_exception",
  "PLpgSQL_stmt_assert",
  "CaseWhen",
]);
const statements = new Set([
  ...decisions,
  "PLpgSQL_stmt_block",
  "PLpgSQL_stmt_assign",
  "PLpgSQL_stmt_execsql",
  "PLpgSQL_stmt_perform",
  "PLpgSQL_stmt_call",
  "PLpgSQL_stmt_case",
  "PLpgSQL_stmt_exit",
  "PLpgSQL_stmt_return",
  "PLpgSQL_stmt_return_next",
  "PLpgSQL_stmt_return_query",
  "PLpgSQL_stmt_raise",
  "PLpgSQL_stmt_getdiag",
  "PLpgSQL_stmt_open",
  "PLpgSQL_stmt_fetch",
  "PLpgSQL_stmt_close",
  "PLpgSQL_stmt_commit",
  "PLpgSQL_stmt_rollback",
]);

/** @param {unknown} value @returns {Record<string,unknown>} */
export function object(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("Native SQL syntax-tree shape was refused.");
  }
  return /** @type {Record<string,unknown>} */ (value);
}

/** Count control-flow decisions, including SQL CASE; Boolean query predicates retain SQL semantics. @param {unknown} tree @returns {number} */
function decisionCount(tree) {
  if (Array.isArray(tree)) {
    return tree.reduce((sum, child) => sum + decisionCount(child), 0);
  }
  if (!tree || typeof tree !== "object") {
    return 0;
  }
  return Object.entries(tree).reduce((sum, [name, child]) => {
    if (name === "CreateFunctionStmt" || name === "DoStmt") {
      throw new Error("Nested executable SQL construction was refused.");
    }
    if (name.startsWith("PLpgSQL_stmt_") && !statements.has(name)) {
      throw new Error("Unsupported procedural SQL statement was refused.");
    }
    if (name === "PLpgSQL_stmt_commit" || name === "PLpgSQL_stmt_rollback") {
      throw new Error("Autonomous SQL authority control was refused.");
    }
    const conditionalExit = name === "PLpgSQL_stmt_exit" && object(child).cond;
    return sum + (decisions.has(name) || conditionalExit ? 1 : 0) + decisionCount(child);
  }, 0);
}

/** @param {Record<string,unknown>} declaration @returns {number} */
function inputParameters(declaration) {
  const parameters = declaration.parameters ?? [];
  if (!Array.isArray(parameters)) {
    throw new Error("Native SQL parameters were refused.");
  }
  return parameters.filter((parameter) => {
    const { mode } = object(object(parameter).FunctionParameter);
    if (
      !new Set([
        "FUNC_PARAM_DEFAULT",
        "FUNC_PARAM_IN",
        "FUNC_PARAM_OUT",
        "FUNC_PARAM_INOUT",
        "FUNC_PARAM_VARIADIC",
        "FUNC_PARAM_TABLE",
      ]).has(String(mode))
    ) {
      throw new Error("Unknown native SQL parameter mode was refused.");
    }
    return !["FUNC_PARAM_OUT", "FUNC_PARAM_TABLE"].includes(String(mode));
  }).length;
}

/** @param {string} source @param {Record<string,unknown>} raw @param {unknown[]} tokens @returns {number} */
function physicalSpan(source, raw, tokens) {
  const bytes = Buffer.from(source);
  const offset = Number(raw.stmt_location ?? 0);
  const length = Number(raw.stmt_len ?? 0);
  if (!Number.isInteger(offset) || offset < 0 || !Number.isInteger(length) || length < 0) {
    throw new Error("Native SQL statement range was refused.");
  }
  const first = tokens.find(
    (token) => Array.isArray(token) && token[0] >= offset && ["CREATE", "DO"].includes(token[2]),
  );
  if (!Array.isArray(first) || first[0] >= (length ? offset + length : bytes.length)) {
    throw new Error("Native SQL executable range was unavailable.");
  }
  return bytes
    .subarray(first[0], length ? offset + length : bytes.length)
    .toString("utf8")
    .trimEnd()
    .split("\n").length;
}

/** @param {string} source @param {unknown} tree @param {SqlLimits} limits @returns {string[]} */
export function sqlFindings(source, tree, limits) {
  const parsed = object(tree);
  const sql = object(parsed.sql);
  if (
    sql.version !== 180006 ||
    !Array.isArray(sql.stmts) ||
    !Array.isArray(parsed.bodies) ||
    !Array.isArray(parsed.tokens)
  ) {
    throw new Error("Native SQL inventory was refused.");
  }
  const executable = sql.stmts.filter((raw) => {
    const stmt = object(object(raw).stmt);
    return stmt.CreateFunctionStmt || stmt.DoStmt;
  });
  if (executable.length !== parsed.bodies.length) {
    throw new Error("Native SQL body inventory differs.");
  }
  const { bodies, tokens } = parsed;
  return executable.flatMap((entry, index) => {
    const raw = object(entry);
    const statement = object(raw.stmt);
    const body = object(bodies[index]);
    if (
      !["plpgsql", "sql"].includes(String(body.language)) ||
      (body.language === "plpgsql" &&
        (!Array.isArray(body.procedural) || body.procedural.length !== 1))
    ) {
      throw new Error("Unsupported or missing executable SQL body was refused.");
    }
    const lines = physicalSpan(source, raw, tokens);
    const parameters = statement.CreateFunctionStmt
      ? inputParameters(object(statement.CreateFunctionStmt))
      : 0;
    const complexity =
      1 +
      decisionCount(statement.CreateFunctionStmt) +
      decisionCount(body.procedural) +
      decisionCount(body.sqlBody) +
      decisionCount(body.expressions);
    return lines > limits.lines || parameters > limits.parameters || complexity > limits.complexity
      ? [
          `${lines} function lines, ${parameters} input parameters, ${complexity} control decisions; limits ${limits.lines}/${limits.parameters}/${limits.complexity}.`,
        ]
      : [];
  });
}
