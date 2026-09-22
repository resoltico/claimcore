import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  standaloneValidatorArtifacts,
  validationWrapper,
  validatorGroups,
  validatorName,
} from "./validator-groups.mjs";

const catalogue = JSON.parse(
  readFileSync(
    new URL("../src/generated/convergence/web-v2.catalog.json", import.meta.url),
    "utf8",
  ),
);
const endpoints = catalogue.endpoints.map((entry) => ({
  endpoint: entry.id,
  exportName: validatorName(entry.id),
}));

test("validator split preserves every endpoint exactly once with discovery isolated", () => {
  const groups = validatorGroups(endpoints);
  assert.deepEqual(Object.keys(groups), ["host", "discovery", "core", "recovery"]);
  assert.deepEqual(groups.host, []);
  assert.deepEqual(
    groups.discovery,
    endpoints.filter((entry) => entry.endpoint === "definition"),
  );
  assert.equal(groups.discovery.length, 1);
  assert.ok(
    groups.core.every(
      (entry) => entry.endpoint !== "definition" && !entry.endpoint.startsWith("recovery."),
    ),
  );
  assert.ok(groups.recovery.every((entry) => entry.endpoint.startsWith("recovery.")));
  assert.deepEqual(
    Object.values(groups)
      .flat()
      .map((entry) => entry.endpoint)
      .sort(),
    endpoints.map((entry) => entry.endpoint).sort(),
  );
});

test("missing validator families fail instead of emitting a partial wrapper", () => {
  for (const omitted of ["discovery", "core", "recovery"]) {
    const groups = validatorGroups(endpoints);
    const remaining = Object.entries(groups)
      .filter(([name]) => name !== omitted)
      .flatMap(([, entries]) => entries);
    assert.throws(() => validatorGroups(remaining), /validator group is empty/u);
  }
});

test("host validation is shared and all four chunks are lazily loaded", () => {
  const wrapper = validationWrapper(validatorGroups(endpoints));
  assert.ok(wrapper.includes('(await loadHost())["validate_host_failure"]'));
  assert.ok(!wrapper.includes('(await validatorsFor(endpoint))["validate_host_failure"]'));
  for (const group of ["host", "discovery", "core", "recovery"]) {
    assert.ok(standaloneValidatorArtifacts.includes(`web-v2.validators.${group}.mjs`));
    assert.ok(standaloneValidatorArtifacts.includes(`web-v2.validators.${group}.d.mts`));
    assert.ok(wrapper.includes(`import("./web-v2.validators.${group}.mjs")`));
    assert.ok(!wrapper.includes(`from "./web-v2.validators.${group}.mjs"`));
  }
});
