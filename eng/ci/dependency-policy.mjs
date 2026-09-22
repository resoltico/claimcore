import assert from "node:assert/strict";

export function version(value) {
  assert(
    typeof value === "string" &&
      /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$/u.test(value),
    "Unsupported dependency version metadata.",
  );
  return value.split(".").map(BigInt);
}
export function newer(left, right) {
  const a = version(left);
  const b = version(right);
  for (let i = 0; i < 3; i += 1) {
    if (a[i] !== b[i]) return a[i] > b[i];
  }
  return false;
}
export const packageKey = (ecosystem, name) =>
  `${ecosystem}|${ecosystem === "nuget" ? name.toLowerCase() : name}`;

export function packageRows(document, collections) {
  assert(
    document &&
      Array.isArray(document.projects) &&
      document.projects.length > 0,
    "Incomplete NuGet metadata response.",
  );
  assert(
    !document.errors?.length && !document.problems?.length,
    "NuGet reported metadata errors.",
  );
  const rows = [];
  for (const project of document.projects) {
    assert(
      project && typeof project === "object",
      "Malformed NuGet project metadata.",
    );
    assert(
      !project.errors?.length && !project.problems?.length,
      "NuGet project metadata failed.",
    );
    if (project.frameworks === undefined) continue;
    assert(Array.isArray(project.frameworks), "Malformed framework metadata.");
    for (const framework of project.frameworks) {
      for (const collection of collections) {
        const values = framework[collection] ?? [];
        assert(Array.isArray(values), "Malformed package collection.");
        rows.push(...values);
      }
    }
  }
  return rows;
}

export function safeFinding(ecosystem, name, current, latest, installed, kind) {
  assert(
    typeof name === "string" &&
      name.length <= 200 &&
      /^@?[A-Za-z0-9][A-Za-z0-9_.\/-]*$/u.test(name),
    "Invalid package identity.",
  );
  assert(
    installed.get(packageKey(ecosystem, name))?.has(current),
    "Package metadata does not match the locked graph.",
  );
  version(current);
  if (latest !== undefined) version(latest);
  return {
    ecosystem,
    package: name,
    current,
    ...(latest === undefined ? {} : { latest }),
    kind,
  };
}

export function validateHolds(
  document,
  installed,
  today = new Date().toISOString().slice(0, 10),
) {
  assert(
    document?.version === 1 && Array.isArray(document.holds),
    "Invalid dependency-hold registry.",
  );
  const keys = new Set();
  for (const hold of document.holds) {
    assert(
      ["nuget", "npm"].includes(hold.ecosystem),
      "Invalid hold ecosystem.",
    );
    safeFinding(
      hold.ecosystem,
      hold.package,
      hold.current,
      hold.latest,
      installed,
      "hold",
    );
    assert(
      hold.current !== hold.latest,
      "A hold must identify an available alternative.",
    );
    assert(
      typeof hold.owner === "string" && hold.owner.trim().length >= 3,
      "A hold requires its owner.",
    );
    assert(
      typeof hold.rationale === "string" && hold.rationale.trim().length >= 20,
      "A hold requires substantive rationale.",
    );
    const dates = [hold.reviewOn, hold.expiresOn].filter(
      (date) => date !== undefined,
    );
    assert(dates.length > 0, "A hold requires a review or expiry date.");
    for (const date of dates) {
      assert(
        typeof date === "string" &&
          /^\d{4}-\d{2}-\d{2}$/u.test(date) &&
          !Number.isNaN(Date.parse(date)) &&
          new Date(date).toISOString().slice(0, 10) === date,
        "Invalid hold date.",
      );
      assert(date >= today, "A dependency hold needs review or has expired.");
    }
    const key = `${packageKey(hold.ecosystem, hold.package)}|${hold.current}|${hold.latest}`;
    assert(!keys.has(key), "Duplicate dependency hold.");
    keys.add(key);
  }
  return keys;
}

export function classifyUpdates(findings, holds) {
  return findings.map((finding) => ({
    ...finding,
    held: holds.has(
      `${packageKey(finding.ecosystem, finding.package)}|${finding.current}|${finding.latest}`,
    ),
  }));
}
