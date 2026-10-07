import { frontendReports } from "./run-frontend-reports.mjs";
// Evidence is admitted by its owning report contract, never by a filename extension.
import assert from "node:assert/strict";
import { existsSync, lstatSync, readFileSync, readdirSync } from "node:fs";
import { join, basename } from "node:path";
import { artifactDirectory } from "./artifact-path.mjs";
import { loadSuites, inventoryPath } from "./suites/registry.mjs";
import { discoverDotnet, parseInventory } from "./suites/inventory.mjs";
import { verifyTrx, verifyPartitions } from "./suites/trx.mjs";
import { parseXml, childrenNamed } from "./suites/xml.mjs";
import { productionAssemblies, reconcileMeasurements } from "./coverage-policy/measurement.mjs";
import { checkFloors, checkCliCoverage, resolveInputs } from "./coverage-policy/policy.mjs";
import { inspectManifest } from "./publish/tree.mjs";
import { producingInputDigest, verifyProducingInputs } from "./publish/inputs.mjs";
import { verifyPublished } from "./publish/main.mjs";
import { verifyDeploymentReport } from "./deployment/report.mjs";
import { assertNoLinkAbove, regularFiles } from "./scan/files.mjs";

/** @param {string} path */
function json(path) {
  safeFile(path);
  return JSON.parse(readFileSync(path, "utf8"));
}
/** @param {string} path */
function safeFile(path) {
  assertNoLinkAbove(path);
  assert.ok(lstatSync(path).isFile(), "Evidence must be a physical regular file.");
}
/** @param {string} path */
function rawCoverage(path) {
  safeFile(path);
  const root = parseXml(readFileSync(path, "utf8"), { allowDoctype: true });
  assert.equal(root.name, "coverage");
  const [packages] = childrenNamed(root, "packages");
  assert.ok(packages);
  const names = childrenNamed(packages, "package").map((item) => item.attributes["name"] ?? "");
  assert.ok(names.length > 0 && names.every((name) => productionAssemblies().includes(name)));
  reconcileMeasurements(root, names);
}
/** @param {string} root @param {import("./suites/registry.mjs").Suite} suite @param {string} base */
function suiteReports(root, suite, base) {
  const assembly = suite.assembly ?? suite.id;
  const names = parseInventory(readFileSync(join(root, inventoryPath(suite)), "utf8"));
  const paths =
    suite.partitions === undefined
      ? [join(base, `${assembly}.trx`)]
      : suite.partitions.ids.map((id) => join(base, id, `${assembly}.${id}.trx`));
  if (!paths.every(existsSync)) {
    return [];
  }
  paths.forEach(safeFile);
  if (suite.partitions === undefined) {
    verifyTrx(readFileSync(paths[0] ?? "", "utf8"), { assembly, names });
  } else {
    const { partitions: partitionNames } = discoverDotnet(root, suite);
    verifyPartitions(
      paths.map((path, index) => ({
        text: readFileSync(path, "utf8"),
        names: partitionNames[suite.partitions?.ids[index] ?? ""] ?? [],
      })),
      { assembly, names },
    );
  }
  return paths;
}
/** @param {string} root @param {string} path */
function architectureReport(root, path) {
  const report = json(path);
  assert.deepEqual(Object.keys(report).sort(), [
    "assemblies",
    "configuration",
    "edges",
    "format",
    "formatVersion",
  ]);
  assert.equal(report.format, "claimcore-architecture-inspection");
  assert.equal(report.formatVersion, 1);
  assert.equal(report.configuration, "Debug");
  const configuration = /** @type {{components:{name:string,tier:string}[]}} */ (
    json(join(root, "config/architecture.json"))
  );
  const components = configuration.components.filter((item) => item.tier === "product");
  const names = components.map((item) => item.name).sort();
  assert.deepEqual(
    report.assemblies.map((/** @type {{name:string}} */ item) => item.name).sort(),
    names,
  );
  for (const item of report.assemblies) {
    assert.deepEqual(Object.keys(item).sort(), ["inspectedTypes", "name"]);
    assert.ok(Number.isSafeInteger(item.inspectedTypes) && item.inspectedTypes > 0);
  }
  for (const item of report.edges) {
    assert.deepEqual(Object.keys(item).sort(), ["source", "target"]);
    assert.ok(names.includes(item.source) && names.includes(item.target));
  }
}
/** @param {string} root @param {string} base @param {import("./suites/registry.mjs").Suite[]} suites */
function dotnetReports(root, base, suites) {
  const admitted = [];
  const input = join(root, "artifacts/coverage/input/dotnet");
  for (const suite of suites.filter(
    (item) => item.kind === "dotnet" && item.group !== "published",
  )) {
    const directory = join(base, suite.id);
    if (!existsSync(directory)) {
      continue;
    }
    const reports = suiteReports(root, suite, directory);
    admitted.push(...reports);
    if (reports.length === 0) {
      continue;
    }
    const graph = join(directory, "architecture-report.json");
    if (suite.id === "architecture" && existsSync(graph)) {
      architectureReport(root, graph);
      admitted.push(graph);
    }

    const prefixes = suite.partitions?.ids.map((id) => `${suite.id}-${id}`) ?? [suite.id];
    const candidates = [
      ...regularFiles(directory),
      ...(existsSync(input) ? regularFiles(input) : []),
    ];
    for (const prefix of prefixes) {
      const coverage = candidates.filter((file) =>
        new RegExp(`^${prefix}\\.coverage\\.cobertura\\.[0-9]{15}\\.xml$`, "u").test(
          basename(file),
        ),
      );
      if (suite.coverage) {
        assert.equal(coverage.length, 1);
      }
      for (const file of coverage) {
        rawCoverage(file);
        admitted.push(file);
      }
    }
  }
  return admitted;
}
/** @param {string} publication */
function publicationReports(publication) {
  assertNoLinkAbove(publication);
  regularFiles(publication);
  verifyPublished(publication);
  return ["cli", "web", "database"].map((product) =>
    join(publication, "manifests", `${product}.json`),
  );
}
/** @param {string} root @param {string} workspace @param {import("./suites/registry.mjs").Suite[]} suites */
function wrapperReports(root, workspace, suites) {
  const admitted = [];
  const publication = join(workspace, "publish");
  if (existsSync(join(publication, "manifests"))) {
    admitted.push(...publicationReports(publication));
  }
  const retained = join(workspace, "manifests");
  if (existsSync(retained)) {
    assert.deepEqual(readdirSync(retained).sort(), ["cli.json", "database.json", "web.json"]);
    for (const [name, product] of [
      ["cli", "ClaimCore.Cli"],
      ["database", "ClaimCore.Database"],
      ["web", "ClaimCore.Web"],
    ]) {
      assert.ok(name && product);
      const path = join(retained, `${name}.json`);
      safeFile(path);
      const manifest = inspectManifest(product, path);
      verifyProducingInputs(manifest.producingInputs, producingInputDigest(root));
      admitted.push(path);
    }
  }
  const results = join(workspace, "results");
  const suite = suites.find((item) => item.id === "acceptance");
  if (suite && existsSync(results)) {
    admitted.push(...suiteReports(root, suite, results));
  }
  const coverage = join(results, "cli.coverage.cobertura.acceptance.xml");
  if (existsSync(coverage)) {
    safeFile(coverage);
    checkCliCoverage(coverage);
    admitted.push(coverage);
  }
  return admitted;
}
/** @param {string} root @param {string} artifacts @param {import("./suites/registry.mjs").Suite[]} suites */
function mergedReports(root, artifacts, suites) {
  const admitted = [];
  const input = join(artifacts, "coverage/input");
  if (existsSync(join(artifacts, "coverage/merged/Cobertura.xml"))) {
    assertNoLinkAbove(input);
    regularFiles(input);
    admitted.push(...resolveInputs(input, suites));
    safeFile(join(artifacts, "coverage/merged/Cobertura.xml"));
    checkFloors(join(artifacts, "coverage/merged/Cobertura.xml"));
    admitted.push(join(artifacts, "coverage/merged/Cobertura.xml"));
    const acceptance = suites.find((item) => item.id === "acceptance");
    if (acceptance) {
      admitted.push(...suiteReports(root, acceptance, join(input, "acceptance")));
    }
  }
  return admitted;
}
/** @param {import("./run-context.mjs").RunContext} context */
export function admittedReports(context) {
  const root = context.source;
  const artifacts = join(root, "artifacts");
  const suites = loadSuites(root);
  const admitted = frontendReports(root);
  const at = context.requested.indexOf("--results-root");
  const results = artifactDirectory(
    root,
    at < 0 ? "artifacts/test-results" : (context.requested[at + 1] ?? ""),
  );
  if (existsSync(results)) {
    admitted.push(...dotnetReports(root, results, suites));
  }
  if (existsSync(artifacts)) {
    for (const name of readdirSync(artifacts)) {
      if (/^(?:acceptance-local|local-browser)\.[A-Za-z0-9]+$/u.test(name)) {
        admitted.push(...wrapperReports(root, join(artifacts, name), suites));
      }
      if (/^claimcore-operating-[0-9a-f]{16}$/u.test(name)) {
        const path = join(artifacts, name, "result.json");
        if (existsSync(path)) {
          verifyDeploymentReport(json(path), {
            revision: context.history.head,
            sourceSha256: context.sourceSha256,
            producingInputsSha256: context.producingInputsSha256,
            runId: "local",
            attempt: "1",
          });
          admitted.push(path);
        }
      }
    }
  }
  admitted.push(...mergedReports(root, artifacts, suites));
  const publication = join(artifacts, "publish");
  if (existsSync(join(publication, "manifests"))) {
    admitted.push(...publicationReports(publication));
  }
  return [...new Set(admitted)].sort();
}
