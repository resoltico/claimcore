// Executable test membership follows imported native MSBuild semantics, never path spelling.
import { spawnSync } from "node:child_process";
import { join } from "node:path";
import { executable } from "../executable.mjs";

/** @typedef {{isTest:boolean,outputType:string,assembly:string}} Classification */
/** @param {string} root @param {string} project @param {string} configuration @param {string[]} properties
 * @returns {Classification} */
export function evaluateTestProject(root, project, configuration, properties) {
  const result = spawnSync(
    executable("dotnet"),
    [
      "msbuild",
      join(root, project),
      "-nologo",
      "-verbosity:quiet",
      `-property:Configuration=${configuration}`,
      ...properties,
      "-getProperty:IsTestProject,OutputType,AssemblyName",
    ],
    { cwd: root, encoding: "utf8", timeout: 30_000, maxBuffer: 1024 * 1024 },
  );
  if (result.status !== 0) {
    throw new Error(`Native test classification failed for ${project}.`);
  }
  const value = JSON.parse(result.stdout).Properties;
  if (
    typeof value?.IsTestProject !== "string" ||
    typeof value.OutputType !== "string" ||
    typeof value.AssemblyName !== "string"
  ) {
    throw new Error(`Native test classification is incomplete for ${project}.`);
  }
  if (value.IsTestProject !== "" && !/^(?:true|false)$/iu.test(value.IsTestProject)) {
    throw new Error(`Native test classification is invalid for ${project}.`);
  }
  return {
    isTest: value.IsTestProject.toLowerCase() === "true",
    outputType: value.OutputType.toLowerCase(),
    assembly: value.AssemblyName,
  };
}

/** @param {string} root @param {string[]} projects @param {import("./registry.mjs").Suite[]} suites
 * @param {typeof evaluateTestProject} [evaluate] @returns {string[]} */
export function projectMembershipProblems(root, projects, suites, evaluate = evaluateTestProject) {
  const errors = [];
  const dotnet = suites.filter((suite) => suite.kind === "dotnet");
  /** @type {Map<string,{configuration:string,properties:string[]}>} */
  const profiles = new Map([
    [
      JSON.stringify({ configuration: "Debug", properties: [] }),
      { configuration: "Debug", properties: [] },
    ],
    [
      JSON.stringify({ configuration: "Release", properties: [] }),
      { configuration: "Release", properties: [] },
    ],
  ]);
  for (const suite of dotnet) {
    const profile = {
      configuration: suite.configuration ?? "Release",
      properties: suite.msbuild ?? [],
    };
    profiles.set(JSON.stringify(profile), profile);
  }
  for (const project of projects) {
    const registered = dotnet.filter((suite) => suite.project === project);
    for (const profile of profiles.values()) {
      let observed;
      try {
        observed = evaluate(root, project, profile.configuration, profile.properties);
      } catch {
        errors.push(`Native test classification failed for ${project} (${profile.configuration}).`);
        continue;
      }
      errors.push(...classificationProblems(project, profile.configuration, observed, registered));
    }
  }
  return [...new Set(errors)];
}

/** @param {string} project @param {string} configuration @param {Classification} observed
 * @param {import("./registry.mjs").Suite[]} registered */
function classificationProblems(project, configuration, observed, registered) {
  const errors = [];
  const executableTest = observed.isTest && ["exe", "winexe"].includes(observed.outputType);
  if (observed.isTest && !executableTest) {
    errors.push(`${project} is test-marked but is not executable (${configuration}).`);
  }
  if (executableTest && registered.length !== 1) {
    errors.push(
      `${project} is an executable test project that ${registered.length === 0 ? "no suite registers" : "multiple suites register"}.`,
    );
  }
  for (const suite of registered) {
    if (!executableTest || suite.assembly !== observed.assembly) {
      errors.push(
        `Suite ${suite.id} has inconsistent test/assembly classification for ${project} (${configuration}).`,
      );
    }
  }
  return errors;
}
