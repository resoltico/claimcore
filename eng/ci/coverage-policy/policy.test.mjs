import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import test from "node:test";
import { checkBrowserCoverage, checkFloors, measuredPrefixes, resolveInputs } from "./policy.mjs";

/** @type {import("../suites/registry.mjs").Suite[]} */
const suites = [
  { id: "unit", kind: "dotnet", coverage: true, platforms: ["linux"] },
  { id: "web", kind: "dotnet", coverage: true, platforms: ["linux"] },
  { id: "docs", kind: "dotnet", coverage: false, platforms: ["linux"] },
  {
    id: "integration",
    kind: "dotnet",
    coverage: true,
    platforms: ["linux"],
    partitions: { selector: "SELECT", ids: ["alpha", "beta"] },
  },
];

const doctype = '<!DOCTYPE coverage SYSTEM "http://cobertura.sourceforge.net/xml/coverage-04.dtd">';

/** @returns {{ root: string, write: (relative: string, content?: string) => string, done: () => void }} */
function fixture() {
  const root = mkdtempSync(join(tmpdir(), "claimcore-coverage-"));
  return {
    root,
    write(relative, content = "synthetic coverage fixture") {
      const path = join(root, relative);
      mkdirSync(dirname(path), { recursive: true });
      writeFileSync(path, content);
      return path;
    },
    done: () => rmSync(root, { recursive: true, force: true }),
  };
}

/**
 * @param {{ line?: string, branch?: string, webLine?: string, webBranch?: string, name?: string, extra?: string, prologue?: string }} [options]
 * @returns {string}
 */
function merged({
  line = "0.60",
  branch = "0.40",
  webLine = "0.80",
  webBranch = "0.70",
  name = "ClaimCore.Web",
  extra = "",
  prologue = "",
} = {}) {
  /** @param {number} count @param {number} hit @param {number} branchCount @param {string} conditions */
  const measured = (count, hit, branchCount, conditions) =>
    Array.from(
      { length: count },
      (_, i) =>
        `<line number="${i + 1}" hits="${i < hit ? 1 : 0}" branch="${i < branchCount}"${i < branchCount ? ` condition-coverage="${conditions}"` : ""}/>`,
    ).join("");
  /** @param {string} pkgName @param {string} lineRate @param {string} branchRate @param {string} lines */
  const pkg = (pkgName, lineRate, branchRate, lines) =>
    `<package name="${pkgName}" line-rate="${lineRate}" branch-rate="${branchRate}"><classes><class name="${pkgName}.Measured"><lines>${lines}</lines></class></classes></package>`;
  const web = pkg(name, webLine, webBranch, measured(100, 80, 10, "70% (7/10)"));
  const domain = pkg("ClaimCore.Domain", "0.5", "0.25", measured(200, 100, 50, "25% (1/4)"));
  return `${prologue}<coverage line-rate="${line}" branch-rate="${branch}" lines-valid="300" lines-covered="180" branches-valid="300" branches-covered="120"><packages>${web}${domain}${extra}</packages></coverage>`;
}

/** @param {string} content */
function floors(content) {
  const files = fixture();
  try {
    return checkFloors(files.write("Cobertura.xml", content), [
      "ClaimCore.Web",
      "ClaimCore.Domain",
    ]);
  } finally {
    files.done();
  }
}

test("floors accept the exact boundary and a standard external doctype", () => {
  assert.deepEqual(floors(merged()), { lineRate: 0.6, branchRate: 0.4, webPackages: 1 });
  assert.equal(floors(merged({ prologue: `${doctype}\n` })).webPackages, 1);
});

test("floors refuse every shortfall and malformed rate", () => {
  const refused = [
    merged({ line: "0.5999" }),
    merged({ branch: "0.3999" }),
    merged({ webLine: "0.7999" }),
    merged({ webBranch: "0.6999" }),
    merged({ name: "ClaimCore.Domain" }),
    merged({
      extra: '<package name="ClaimCore.Web.Routes" line-rate="0.7999" branch-rate="0.70"/>',
    }),
    merged({ line: "NaN" }),
    merged({ webBranch: "1.01" }),
    '<!DOCTYPE coverage [<!ENTITY value "0.80">]><coverage line-rate="0.60" branch-rate="0.40"><packages><package name="ClaimCore.Web" line-rate="&value;" branch-rate="0.70"/></packages></coverage>',
  ];
  for (const content of refused) {
    assert.throws(() => floors(content), /./u, content);
  }
});

const webClass =
  '<classes><class name="ClaimCore.Web.Program"><lines><line branch="True" condition-coverage="50% (1/2)"/></lines></class></classes>';

/**
 * @param {string} counters
 * @param {string} packages
 * @returns {string}
 */
const browser = (counters, packages) =>
  `<coverage ${counters}><packages>${packages}</packages></coverage>`;
const measured = browser(
  'branches-covered="1" branches-valid="2"',
  `<package name="ClaimCore.Web" branch-rate="0.5">${webClass}</package>`,
);

/** @param {string} content */
function browserVerdict(content) {
  const files = fixture();
  try {
    checkBrowserCoverage(files.write("chromium.coverage.cobertura.e2e.xml", content));
  } finally {
    files.done();
  }
}

test("browser coverage must measure ClaimCore.Web branches", () => {
  browserVerdict(measured);
  const refused = [
    browser('branches-covered="0" branches-valid="0"', ""),
    browser(
      'branches-covered="0" branches-valid="2"',
      `<package name="ClaimCore.Web" branch-rate="0">${webClass}</package>`,
    ),
    browser(
      'branches-covered="1" branches-valid="2"',
      '<package name="ClaimCore.Domain" branch-rate="0.5"><classes><class name="ClaimCore.Domain.Claim"/></classes></package>',
    ),
    browser(
      'branches-covered="1" branches-valid="2"',
      `<package name="ClaimCore.Web" branch-rate="0">${webClass}</package>`,
    ),
    browser(
      'branches-covered="1" branches-valid="2"',
      '<package name="ClaimCore.Web" branch-rate="0.5"><classes><class name="ClaimCore.Web.Program"><lines><line branch="True" condition-coverage="0% (0/2)"/></lines></class></classes></package>',
    ),
    `<!DOCTYPE coverage [<!ENTITY rate "0.5">]><coverage branches-covered="1" branches-valid="2"><packages><package name="ClaimCore.Web" branch-rate="&rate;">${webClass}</package></packages></coverage>`,
  ];
  for (const content of refused) {
    assert.throws(() => browserVerdict(content), /./u, content);
  }
});

test("measured prefixes follow the registered suites and partitions", () => {
  assert.deepEqual(measuredPrefixes(suites), [
    "unit",
    "web",
    "integration-alpha",
    "integration-beta",
  ]);
});

/** @returns {ReturnType<typeof fixture>} A directory holding every expected report. */
function completeInputs() {
  const files = fixture();
  files.write("unit/unit.coverage.cobertura.202609090000001.xml");
  files.write("web/web.coverage.cobertura.202609090000002.xml");
  files.write("integration/integration-alpha.coverage.cobertura.202609090000003.xml");
  files.write("elsewhere/integration-beta.coverage.cobertura.202609090000004.xml");
  files.write(
    "acceptance/cli.coverage.cobertura.acceptance.xml",
    measured.replaceAll("ClaimCore.Web", "ClaimCore.Cli"),
  );
  for (const engine of ["chromium", "firefox", "webkit"]) {
    files.write(`browser/${engine}.coverage.cobertura.e2e.xml`, measured);
  }
  return files;
}

test("inputs resolve every registered report wherever it was unpacked", () => {
  const files = completeInputs();
  try {
    assert.equal(resolveInputs(files.root, suites).length, 8);
  } finally {
    files.done();
  }
});

/** @typedef {[string, (files: ReturnType<typeof fixture>) => void]} Mutation */

/** @type {Mutation[]} */
const duplicateAndUnexpected = [
  ["unexpected report", (files) => files.write("browser/extra.coverage.cobertura.injected.xml")],
  ["duplicate role", (files) => files.write("unit/unit.coverage.cobertura.202609090000009.xml")],
  [
    "duplicate partition",
    (files) => files.write("integration/integration-alpha.coverage.cobertura.202609090000099.xml"),
  ],
  [
    "unregistered partition",
    (files) => files.write("integration/integration-gamma.coverage.cobertura.202609090000098.xml"),
  ],
];

/** @type {Mutation[]} */
const missingAndMalformed = [
  [
    "untimestamped report",
    (files) => {
      rmSync(join(files.root, "unit/unit.coverage.cobertura.202609090000001.xml"));
      files.write("unit/unit.coverage.cobertura.latest.xml");
    },
  ],
  [
    "missing partition",
    (files) =>
      rmSync(join(files.root, "elsewhere/integration-beta.coverage.cobertura.202609090000004.xml")),
  ],
  [
    "missing engine",
    (files) => rmSync(join(files.root, "browser/webkit.coverage.cobertura.e2e.xml")),
  ],
  [
    "empty engine report",
    (files) =>
      files.write(
        "browser/firefox.coverage.cobertura.e2e.xml",
        '<coverage branches-covered="0" branches-valid="0"><packages/></coverage>',
      ),
  ],
];

test("inputs refuse a missing, duplicate or unexpected report", () => {
  for (const [label, mutate] of [...duplicateAndUnexpected, ...missingAndMalformed]) {
    const files = completeInputs();
    try {
      mutate(files);
      assert.throws(() => resolveInputs(files.root, suites), /./u, label);
    } finally {
      files.done();
    }
  }
});

test("claimed perfect coverage cannot replace measured production data", () => {
  for (const content of [
    '<coverage line-rate="1" branch-rate="1"><packages><package name="ClaimCore.Web" line-rate="1" branch-rate="1"/></packages></coverage>',
    merged().replace('lines-covered="180"', 'lines-covered="301"'),
    merged().replaceAll('hits="1"', 'hits="0"'),
    merged().replace('number="2"', 'number="1"'),
    merged({
      extra: '<package name="ClaimCore.ContractGenerator" line-rate="1" branch-rate="1"/>',
    }),
    merged().replace('branches-valid="300"', 'branches-valid="100"'),
  ]) {
    assert.throws(() => floors(content));
  }
});
