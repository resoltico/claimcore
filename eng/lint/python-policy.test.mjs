import assert from "node:assert/strict";
import test from "node:test";
import { findings } from "./test-support.mjs";

/**
 * @param {string} select
 * @param {string} strict
 * @param {string} [pylint]
 */
function pyproject(
  select,
  strict,
  pylint = "max-args = 8\nmax-positional-args = 5\nmax-statements = 50",
) {
  return `[tool.ruff.lint]\nselect = ${select}\n[tool.ruff.lint.mccabe]\nmax-complexity = 12\n[tool.ruff.lint.pylint]\n${pylint}\n[tool.mypy]\nstrict = ${strict}\n`;
}

test("ruff must select every rule and mypy must run strict", () => {
  assert.deepEqual(findings({ "pyproject.toml": pyproject('["ALL"]', "true") }, []), []);
  assert.ok(
    findings({ "pyproject.toml": pyproject('["E", "F"]', "true") }, []).some((e) =>
      /select exactly \["ALL"\]/u.test(e),
    ),
  );
  assert.ok(
    findings({ "pyproject.toml": pyproject('["ALL"]', "false") }, []).some((e) =>
      /strict = true/u.test(e),
    ),
  );
});

test("ruff pylint size ceilings are fixed", () => {
  for (const [pylint, name] of /** @type {Array<[string, string]>} */ ([
    ["max-args = 9\nmax-positional-args = 5\nmax-statements = 50", "max-args"],
    ["max-args = 8\nmax-positional-args = 6\nmax-statements = 50", "max-positional-args"],
    ["max-args = 8\nmax-positional-args = 5", "max-statements"],
  ])) {
    assert.ok(
      findings({ "pyproject.toml": pyproject('["ALL"]', "true", pylint) }, []).some((e) =>
        e.includes(`'${name}'`),
      ),
      name,
    );
  }
});

test("a Python file over the size limit cannot be registered away", () => {
  const errors = findings({ "eng/backup/big.py": "x = 1\n".repeat(301) }, []);
  assert.ok(errors.some((error) => /301 physical lines/u.test(error)));
});
