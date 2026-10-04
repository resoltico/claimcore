import assert from "node:assert/strict";
import test from "node:test";
import { licenseProjectComponents } from "./main.mjs";

test("project SBOM license projection preserves similarly named dependencies", () => {
  const dependency = {
    name: "ClaimCore.ExternalDependency",
    licenses: [{ license: { id: "MIT" } }],
  };
  const original = structuredClone(dependency);
  const sbom = {
    metadata: { component: { name: "ClaimCore.Cli" } },
    components: [{ name: "ClaimCore.Domain" }, dependency],
  };
  const result = licenseProjectComponents(sbom, "MPL-2.0");
  assert.deepEqual(result.metadata?.component?.licenses, [{ expression: "MPL-2.0" }]);
  assert.deepEqual(result.components?.[0]?.licenses, [{ expression: "MPL-2.0" }]);
  assert.deepEqual(result.components?.[1], original);
});

test("missing, unregistered or unlicensed product metadata refuses publication", () => {
  for (const component of [{}, { name: "ClaimCore.ExternalDependency" }]) {
    assert.throws(
      () => licenseProjectComponents({ metadata: { component } }, "MPL-2.0"),
      /registered product/u,
    );
  }
  assert.throws(() => licenseProjectComponents({}, "MPL-2.0"), /registered product/u);
  assert.throws(
    () => licenseProjectComponents({ metadata: { component: { name: "ClaimCore.Cli" } } }, ""),
    /project license/u,
  );
});
