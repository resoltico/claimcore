// Executable test membership follows imported native MSBuild semantics, never path spelling.
import { spawnSync } from "node:child_process";
import { join } from "node:path";
import { executable } from "../executable.mjs";

const errorCodes = new Set([
  "ENOENT",
  "EACCES",
  "EPERM",
  "ETIMEDOUT",
  "ENOBUFS",
  "E2BIG",
  "EINVAL",
  "ENOMEM",
  "EIO",
  "EBADF",
]);
const signals = new Set([
  "SIGTERM",
  "SIGKILL",
  "SIGABRT",
  "SIGSEGV",
  "SIGINT",
  "SIGBUS",
  "SIGILL",
  "SIGFPE",
  "SIGQUIT",
  "SIGHUP",
  "SIGPIPE",
  "SIGBREAK",
]);
/** @param {import("node:child_process").SpawnSyncReturns<string>} result */
function nativeCode(result) {
  const { error } = result;
  const code = error && "code" in error ? error.code : undefined;
  return typeof code === "string" && errorCodes.has(code) ? code : "unknown";
}
/** @typedef {"start"|"process-error"|"deadline"|"capture-limit"|"exit"|"property-json"|"property-incomplete"|"property-invalid"} FailureReason */
class ClassificationFailure extends Error {
  /** @param {FailureReason} reason @param {import("node:child_process").SpawnSyncReturns<string>} result @param {number} elapsed */
  constructor(reason, result, elapsed) {
    const code = nativeCode(result);
    const exit =
      Number.isInteger(result.status) &&
      result.status !== null &&
      result.status >= -2147483648 &&
      result.status <= 4294967295
        ? result.status
        : "unknown";
    const signal =
      typeof result.signal === "string" && signals.has(result.signal) ? result.signal : "unknown";

    const milliseconds =
      Number.isFinite(elapsed) && elapsed >= 0 && elapsed <= 3600000
        ? Math.round(elapsed)
        : "unknown";
    super(
      `reason=${reason}; exit=${exit}; signal=${signal}; errorCode=${code}; elapsedMs=${milliseconds}`,
    );
  }
}
/** @param {import("node:child_process").SpawnSyncReturns<string>} result @returns {FailureReason} */
function processFailure(result) {
  if (nativeCode(result) === "ETIMEDOUT") {
    return "deadline";
  }
  if (nativeCode(result) === "ENOBUFS") {
    return "capture-limit";
  }
  if (result.error) {
    return ["ENOENT", "EACCES", "EPERM", "E2BIG"].includes(nativeCode(result))
      ? "start"
      : "process-error";
  }
  return "exit";
}
/** @param {string} text @param {()=>never} refuse */
function propertyValues(text, refuse) {
  try {
    return JSON.parse(text)?.Properties;
  } catch {
    return refuse();
  }
}

/** @typedef {{isTest:boolean,outputType:string,assembly:string}} Classification */
/** @param {string} root @param {string} project @param {string} configuration @param {string[]} properties
 * @param {(command:string,args:string[],options:import("node:child_process").SpawnSyncOptionsWithStringEncoding)=>import("node:child_process").SpawnSyncReturns<string>} [run]
 * @returns {Classification} */
export function evaluateTestProject(root, project, configuration, properties, run = spawnSync) {
  const started = performance.now();
  const result = run(
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
  const elapsed = performance.now() - started;
  /** @param {FailureReason} reason @returns {never} */
  const refuse = (reason) => {
    throw new ClassificationFailure(reason, result, elapsed);
  };
  if (result.error || result.status !== 0) {
    refuse(processFailure(result));
  }
  const value = propertyValues(result.stdout, () => refuse("property-json"));
  if (
    typeof value?.IsTestProject !== "string" ||
    typeof value.OutputType !== "string" ||
    typeof value.AssemblyName !== "string"
  ) {
    refuse("property-incomplete");
  }
  if (value.IsTestProject !== "" && !/^(?:true|false)$/iu.test(value.IsTestProject)) {
    refuse("property-invalid");
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
      } catch (error) {
        const facts = error instanceof ClassificationFailure ? error.message : "reason=unknown";
        errors.push(
          `Native test classification failed for ${project} (${profile.configuration}); ${facts}.`,
        );
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
