// How one registered suite becomes test processes: their arguments, environment and result files.
// Pure planning, so every command line can be tested without running a test.
import { join } from "node:path";

/**
 * @typedef {object} Job
 * @property {string} suite Suite id.
 * @property {string} assembly
 * @property {string | undefined} partition
 * @property {string[]} args `dotnet` arguments.
 * @property {NodeJS.ProcessEnv} env Extra environment.
 * @property {string} results Directory the process writes its reports to.
 * @property {string} trx Report file name inside `results`.
 * @property {string[]} names The tests the report must name exactly.
 * @property {string} binaryDirectory Directory of the selected configuration's built assemblies.
 * @property {string | undefined} privateBin Measured copy of the test binaries, when one is made.
 */

/**
 * @param {import("./registry.mjs").Suite} suite
 * @param {{ results: string, trx: string, count: number, coveragePrefix: string | undefined }} run
 * @param {string} root
 * @returns {{ args: string[], privateBin: string | undefined }}
 */
function dotnetArguments(suite, { results, trx, count, coveragePrefix }, root) {
  const common = [
    `--results-directory=${results}`,
    `--minimum-expected-tests=${count}`,
    "--zero-tests-policy=strict",
    `--timeout=${suite.timeout}`,
  ];
  const settings = [
    `--settings=${join(root, "eng/expecto.runsettings")}`,
    "--report-trx",
    `--report-trx-filename=${trx}`,
  ];
  const configuration = suite.configuration ?? "Release";
  if (suite.partitions !== undefined && coveragePrefix !== undefined) {
    // Coverlet rewrites assemblies while it measures, so each concurrent measured process runs its
    // own copy of the output directory, inside the repository so product processes find its root.
    const privateBin = join(results, "measured-bin");
    return {
      privateBin,
      args: [
        join(privateBin, `${suite.assembly}.dll`),
        ...common,
        ...settings,
        "--coverlet",
        `--coverlet-file-prefix=${coveragePrefix}`,
      ],
    };
  }
  return {
    privateBin: undefined,
    args: [
      "test",
      "--project",
      suite.project ?? "",
      "--configuration",
      configuration,
      "--no-build",
      "--no-restore",
      "--max-parallel-test-modules",
      "1",
      ...common,
      "--",
      ...settings,
      ...(coveragePrefix === undefined
        ? []
        : ["--coverlet", `--coverlet-file-prefix=${coveragePrefix}`]),
    ],
  };
}

/**
 * @param {string} id
 * @param {string | undefined} partition
 * @returns {string}
 */
const coveragePrefix = (id, partition) => (partition === undefined ? id : `${id}-${partition}`);

/**
 * @param {string} root
 * @param {import("./registry.mjs").Suite} suite
 * @param {string | undefined} partition
 * @param {string} results
 * @returns {NodeJS.ProcessEnv}
 */
function environment(root, suite, partition, results) {
  /** @type {NodeJS.ProcessEnv} */
  const env = Object.fromEntries(
    Object.entries(suite.env ?? {}).map(([key, value]) => [
      key,
      value.replaceAll("{root}", root).replaceAll("{results}", results),
    ]),
  );
  if (suite.partitions && partition) {
    env[suite.partitions.selector] = partition;
  }
  return env;
}

/**
 * The processes that run one suite: one, or one per registered partition.
 * @param {string} root
 * @param {import("./registry.mjs").Suite} suite
 * @param {{ resultsRoot: string, names: string[], partitionNames: Record<string, string[]> }} inputs
 * @returns {Job[]}
 */
export function planSuite(root, suite, { resultsRoot, names, partitionNames }) {
  const assembly = suite.assembly ?? suite.id;
  const base = join(resultsRoot, suite.id);
  const coverage = suite.coverage === true;
  /** @param {string | undefined} partition @param {string[]} owned */
  const job = (partition, owned) => {
    const results = partition === undefined ? base : join(base, partition);
    const trx = partition === undefined ? `${assembly}.trx` : `${assembly}.${partition}.trx`;
    const prefix = coverage ? coveragePrefix(suite.id, partition) : undefined;
    const { args, privateBin } = dotnetArguments(
      suite,
      { results, trx, count: owned.length, coveragePrefix: prefix },
      root,
    );
    /** @type {Job} */
    const planned = {
      suite: suite.id,
      assembly,
      partition,
      args,
      env: environment(root, suite, partition, results),
      results,
      trx,
      names: owned,
      privateBin,
      binaryDirectory: join(
        root,
        "artifacts/bin",
        assembly,
        (suite.configuration ?? "Release").toLowerCase(),
      ),
    };
    return planned;
  };
  if (suite.partitions === undefined) {
    return [job(undefined, names)];
  }
  return suite.partitions.ids.map((id) => job(id, partitionNames[id] ?? []));
}
