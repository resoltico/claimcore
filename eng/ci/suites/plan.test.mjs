import assert from "node:assert/strict";
import { join } from "node:path";
import test from "node:test";
import { planSuite } from "./plan.mjs";

const root = "/repo";
const resultsRoot = "/repo/artifacts/test-results";
/** @type {import("./registry.mjs").Suite} */
const unit = {
  id: "unit",
  kind: "dotnet",
  assembly: "Example.Tests",
  project: "tests/Example.Tests/Example.Tests.fsproj",
  configuration: "Release",
  coverage: true,
  platforms: ["linux"],
  timeout: "25m",
};

test("an unpartitioned suite is one dotnet test process that counts exactly its inventory", () => {
  const [job, ...others] = planSuite(root, unit, {
    resultsRoot,
    names: ["a", "b", "c"],
    partitionNames: {},
  });
  assert.equal(others.length, 0);
  assert.ok(job);
  assert.equal(job.results, join(resultsRoot, "unit"));
  assert.equal(job.trx, "Example.Tests.trx");
  assert.deepEqual(job.args.slice(0, 5), [
    "test",
    "--project",
    unit.project,
    "--configuration",
    "Release",
  ]);
  assert.ok(job.args.includes("--minimum-expected-tests=3"));
  assert.ok(job.args.includes("--zero-tests-policy=strict"));
  assert.ok(job.args.includes("--no-build"));
  const separator = job.args.indexOf("--");
  assert.deepEqual(job.args.slice(separator + 2), [
    "--report-trx",
    "--report-trx-filename=Example.Tests.trx",
    "--coverlet",
    "--coverlet-file-prefix=unit",
  ]);
  assert.equal(job.privateBin, undefined);
});

test("a suite without coverage never asks for it", () => {
  const [job] = planSuite(
    root,
    { ...unit, coverage: false },
    { resultsRoot, names: ["a"], partitionNames: {} },
  );
  assert.ok(job);
  assert.ok(!job.args.some((argument) => argument.includes("coverlet")));
});

test("each partition is its own measured process with its own selector, count and binaries", () => {
  const suite = {
    ...unit,
    id: "integration",
    partitions: { selector: "EXAMPLE_PARTITION", ids: ["one", "two"] },
  };
  const jobs = planSuite(root, suite, {
    resultsRoot,
    names: ["a", "b", "c"],
    partitionNames: { one: ["a"], two: ["b", "c"] },
  });
  assert.deepEqual(
    jobs.map((job) => [job.partition, job.env, job.names.length, job.trx]),
    [
      ["one", { EXAMPLE_PARTITION: "one" }, 1, "Example.Tests.one.trx"],
      ["two", { EXAMPLE_PARTITION: "two" }, 2, "Example.Tests.two.trx"],
    ],
  );
  const [first, second] = jobs;
  assert.ok(first && second);
  assert.notEqual(first.privateBin, second.privateBin);
  assert.equal(first.args[0], join(first.privateBin ?? "", "Example.Tests.dll"));
  assert.ok(second.args.includes("--minimum-expected-tests=2"));
  assert.ok(second.args.includes("--coverlet-file-prefix=integration-two"));
  assert.equal(first.results, join(resultsRoot, "integration", "one"));
});
