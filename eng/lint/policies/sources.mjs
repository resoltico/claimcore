const sizeLimit = 300;
const sized = /\.(?:fs|fsi|fsx|ts|tsx|js|mjs|cjs|css|ps1|psm1|sh|py|yml|yaml)$/u;
const fsharpTests = /^tests\/.*\.(?:fs|fsx|fsi)$/u;
const webTests = /^web\/(?:tests|e2e)\//u;
const pythonTests = /^eng\/.*Test-[^/]*\.py$/u;
const playwright = /^web\/playwright\.config\./u;
const vite = /^web\/(?:vite|vitest)\.config\./u;
const commands =
  /^\.github\/workflows\/[^/]+\.ya?ml$|^eng\/ci\/(?:stage-plans\/[^/]+\.json|local-plan\.json)$/u;

/**
 * A line-level rule that no registry entry can relax.
 * @typedef {object} LineRule
 * @property {RegExp} files Paths the rule applies to.
 * @property {RegExp} pattern What must not appear.
 * @property {string} message
 */

/** @type {LineRule[]} */
const lineRules = [
  {
    files: fsharpTests,
    pattern:
      /(?:\[<\s*[FP]Tests(?:Attribute)?\s*>\]|\b(?:f|p)test(?:Case(?:Async|Task|WithCancel)?|Async|Task|Theory(?:Async|Task)?|List)?\b|\bskiptestf?\b|\bFocusState\.(?:Focused|Pending)\b)/iu,
    message: "contains non-suppressible focused or skipped F# test code",
  },
  {
    files: fsharpTests,
    pattern: /%[AO]/u,
    message: "contains non-suppressible structural formatting that can serialize test payloads",
  },
  {
    files: webTests,
    pattern:
      /\b(?:describe|it|test)(?:\.describe)?\.(?:only|skip|todo|fails|runIf|skipIf|fixme|fail|slow)\b|\b(?:fdescribe|fit|xdescribe|xit|xtest)\b/iu,
    message:
      "contains non-suppressible focused, skipped, expected-failure, or conditional JavaScript/TypeScript test code",
  },
  {
    files: webTests,
    pattern:
      /\b(?:testInfo|test\.info\(\))\.(?:annotations|fail|fixme|skip|slow)\b|\bannotations?\s*:/iu,
    message: "contains a non-suppressible test annotation or outcome escape",
  },
  {
    files: webTests,
    pattern: /\b(?:describe|test)(?:\.describe)?\.configure\s*\(|\b(?:retry|retries|fails)\s*:/iu,
    message: "contains a non-suppressible retry or expected-failure escape",
  },
  {
    files: pythonTests,
    pattern:
      /\b(?:unittest\.(?:skip\w*|expectedFailure)|pytest\.mark\.(?:skip\w*|xfail)|\.skipTest\s*\(|@skip\w*|@expectedFailure)\b/u,
    message: "contains non-suppressible skipped or expected-failure Python test code",
  },
  {
    files: commands,
    pattern:
      /(?:--filter(?:-uid)?\b|--treenode-filter\b|\bTestCaseFilter\b|--grep(?:-invert)?\b|--shard\b|--last-failed\b|--only-changed\b)/iu,
    message: "contains a non-suppressible required-CI test filter",
  },
  {
    files: /^web\/package\.json$/u,
    pattern: /--(?:passWithNoTests|changed|related|shard|grep(?:-invert)?)\b/iu,
    message: "contains a non-suppressible test-selection escape",
  },
  {
    files: playwright,
    pattern: /\b(?:grep|grepInvert|shard|annotations?)\s*:/iu,
    message: "contains a non-suppressible Playwright selection or annotation escape",
  },
  {
    files: playwright,
    pattern: /\bretries\s*:\s*(?!0\b)[0-9]+/iu,
    message: "enables Playwright retries; required evidence must be zero-retry",
  },
  {
    files: vite,
    pattern: /\bretry\s*:\s*(?!0\b)[0-9]+/iu,
    message: "enables Vitest retries; required evidence must be zero-retry",
  },
];

/**
 * Rules that no registry entry can relax.
 * @param {import("../files.mjs").SourceFile} source
 * @param {import("../model.mjs").Report} report
 */
export function checkSource(source, report) {
  const { path, lines } = source;
  const physical = lines.at(-1) === "" ? lines.length - 1 : lines.length;
  if (sized.test(path) && physical > sizeLimit) {
    report.add(
      `${path} has ${physical} physical lines; the repository maximum is ${sizeLimit}. Split the responsibility instead of suppressing the limit.`,
    );
  }
  for (const rule of lineRules.filter((candidate) => candidate.files.test(path))) {
    lines.forEach((line, index) => {
      if (rule.pattern.test(line)) {
        report.add(`${path}:${index + 1} ${rule.message}.`);
      }
    });
  }
}
