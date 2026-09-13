import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const webDirectory = resolve(scriptDirectory, "..");
const outputDirectory = resolve(webDirectory, "../artifacts/sbom");
const outputFile = resolve(outputDirectory, "claimcore-web.cdx.json");
const npmCli = process.env["npm_execpath"];
const rootDirectory = resolve(webDirectory, "..");

const productionRootReferences = (packageLock) => {
  const rootPackage = packageLock.packages?.[""];
  if (rootPackage === undefined) throw new Error("npm lock root metadata is required.");
  return Object.keys(rootPackage.dependencies ?? {})
    .map((name) => {
      const locked = packageLock.packages[`node_modules/${name}`];
      if (locked?.version === undefined || locked.name !== undefined) {
        throw new Error(`Production dependency ${name} must have an exact non-aliased lock entry.`);
      }
      return `${name}@${locked.version}`;
    })
    .sort();
};

const reachableReferences = (roots, components, dependencies) => {
  const retained = new Set();
  const pending = [...roots];
  while (pending.length > 0) {
    const reference = pending.pop();
    if (retained.has(reference)) continue;
    if (!components.has(reference)) {
      throw new Error(`npm SBOM omits production component ${reference}.`);
    }
    retained.add(reference);
    pending.push(...(dependencies.get(reference)?.dependsOn ?? []));
  }
  return retained;
};

const productionClosure = (document, packageLock) => {
  const rootGraph = document.dependencies?.find(
    (dependency) => dependency.ref === document.metadata?.component?.["bom-ref"],
  );
  if (rootGraph === undefined) throw new Error("npm SBOM root metadata is required.");

  const components = new Map(
    (document.components ?? []).map((component) => [component["bom-ref"], component]),
  );
  const dependencies = new Map(
    document.dependencies.map((dependency) => [dependency.ref, dependency]),
  );
  const roots = productionRootReferences(packageLock);
  const retained = reachableReferences(roots, components, dependencies);

  document.components = document.components.filter((component) =>
    retained.has(component["bom-ref"]),
  );
  document.dependencies = [
    { ...rootGraph, dependsOn: roots },
    ...document.dependencies
      .filter((dependency) => retained.has(dependency.ref))
      .map((dependency) => ({
        ...dependency,
        dependsOn: (dependency.dependsOn ?? []).filter((reference) => retained.has(reference)),
      })),
  ];
  return document;
};

if (npmCli === undefined || npmCli.length === 0) {
  throw new Error("Run SBOM generation through the pinned npm package manager.");
}

mkdirSync(outputDirectory, { recursive: true });
const props = readFileSync(resolve(rootDirectory, "Directory.Build.props"), "utf8");
const version = props.match(/<Version>([^<]+)<\/Version>/u)?.[1];

if (version === undefined)
  throw new Error("Directory.Build.props must define the product version.");

const temporary = mkdtempSync(resolve(tmpdir(), "claimcore-sbom-"));
try {
  const manifest = JSON.parse(readFileSync(resolve(webDirectory, "package.json"), "utf8"));
  const packageLock = JSON.parse(readFileSync(resolve(webDirectory, "package-lock.json"), "utf8"));
  manifest.version = version;
  packageLock.version = version;
  packageLock.packages[""].version = version;
  writeFileSync(resolve(temporary, "package.json"), JSON.stringify(manifest));
  writeFileSync(resolve(temporary, "package-lock.json"), JSON.stringify(packageLock));
  const sbom = execFileSync(
    process.execPath,
    [npmCli, "sbom", "--sbom-format", "cyclonedx", "--package-lock-only"],
    { cwd: temporary, encoding: "utf8", maxBuffer: 16 * 1024 * 1024 },
  );
  const document = productionClosure(JSON.parse(sbom), packageLock);
  const component = document.metadata?.component;
  const expectedReference = `${manifest.name}@${version}`;
  const declaredLicenses = component?.licenses?.map((entry) => entry.license?.id) ?? [];
  if (
    component?.["bom-ref"] !== expectedReference ||
    component.purl !== `pkg:npm/${expectedReference}` ||
    !declaredLicenses.includes(manifest.license)
  ) {
    throw new Error("npm SBOM root identity or license does not match the product manifest.");
  }
  component.name = manifest.name;
  writeFileSync(outputFile, `${JSON.stringify(document, null, 2)}\n`, {
    encoding: "utf8",
    mode: 0o600,
  });
} finally {
  rmSync(temporary, { recursive: true, force: true });
}
