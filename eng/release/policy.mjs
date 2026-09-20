import assert from "node:assert/strict";

export const isVersion = (value) =>
  typeof value === "string" &&
  value === value.trim() &&
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/u.test(value);

export const isSha = (value) =>
  typeof value === "string" && value.length === 40 && /^[a-f0-9]+$/u.test(value);

/** Only transport newlines and trailing empty lines are normalized. */
export const canonicalNotes = (text) => {
  assert.equal(typeof text, "string", "Release notes must be text.");
  const normalized = text.replaceAll("\r\n", "\n");
  assert(!normalized.includes("\r"), "Bare CR characters are not supported.");
  return normalized.replace(/\n+$/u, "");
};

const heading = /^ {0,3}##(?:[ \t]|$)/u;

const headingsOutsideFences = (lines) => {
  const headings = [];
  let fence = null;

  for (const [index, line] of lines.entries()) {
    if (fence !== null) {
      const close = /^ {0,3}(`+|~+)[ \t]*$/u.exec(line);
      if (close !== null && close[1][0] === fence[0] && close[1].length >= fence.length) {
        fence = null;
      }
      continue;
    }

    const open = /^ {0,3}(`{3,}|~{3,})(.*)$/u.exec(line);
    if (open !== null) {
      assert(open[1][0] !== "`" || !open[2].includes("`"), "Malformed code fence.");
      fence = open[1];
      continue;
    }

    assert(!line.includes("<!--"), "HTML comments outside fenced code are not supported.");
    if (heading.test(line)) headings.push({ index, line });
  }

  assert.equal(fence, null, "Unclosed code fence in CHANGELOG.md.");
  return headings;
};

const labels = (text) =>
  [...text.matchAll(/\[([^\]\n]+)\]/gu)].map((match) =>
    match[1].trim().replace(/\s+/gu, " ").toLowerCase(),
  );

const definitions = (text) =>
  [...text.matchAll(/^ {0,3}\[([^\]\n]+)\]:/gmu)].map((match) =>
    match[1].trim().replace(/\s+/gu, " ").toLowerCase(),
  );

const assertSelfContainedReferences = (changelog, section) => {
  const local = new Set(definitions(section));
  const used = new Set(labels(section));

  for (const label of definitions(changelog)) {
    assert(
      !used.has(label) || local.has(label),
      `Reference [${label}] is defined outside this release section.`,
    );
  }
};

/**
 * Validates one dated Keep-a-Changelog section and returns its body without the heading.
 * GitHub supplies the release title and publication time, so neither belongs in the body.
 */
export const extractReleaseBody = (changelog, version) => {
  assert(isVersion(version), "Expected a stable X.Y.Z version.");
  const source = canonicalNotes(changelog);
  const lines = source.split("\n");
  const escaped = version.replaceAll(".", "\\.");
  const releaseHeading = new RegExp(
    `^ {0,3}##[ \\t]+\\[${escaped}\\][ \\t]+-[ \\t]+\\d{4}-\\d{2}-\\d{2}$`,
    "u",
  );
  const candidates = headingsOutsideFences(lines).filter(({ line }) => releaseHeading.test(line));
  assert.equal(candidates.length, 1, "Release section must exist exactly once.");

  const selected = candidates[0];
  const date = /^ {0,3}## \[[^\]]+\] - (\d{4}-\d{2}-\d{2})$/u.exec(selected.line);
  assert(date !== null, "Expected: ## [X.Y.Z] - YYYY-MM-DD");
  const parsed = new Date(`${date[1]}T00:00:00Z`);
  assert(
    !Number.isNaN(parsed.valueOf()) && parsed.toISOString().slice(0, 10) === date[1],
    "Invalid release date.",
  );

  const next = headingsOutsideFences(lines).find(({ index }) => index > selected.index);
  const bodyLines = lines.slice(selected.index + 1, next?.index ?? lines.length);
  while (bodyLines[0] === "") bodyLines.shift();
  const body = canonicalNotes(bodyLines.join("\n"));
  assert(
    body.split("\n").some((line) => line.trim() && !/^\s*#|^ {0,3}\[[^\]]+\]:/u.test(line)),
    "Empty release section.",
  );
  assertSelfContainedReferences(source, body);
  return body;
};

/** Reads the one literal, unconditional Version directly owned by Directory.Build.props. */
export const declaredVersion = (xml) => {
  assert.equal(typeof xml, "string", "Directory.Build.props must be text.");
  assert(!/<!DOCTYPE|<!ENTITY|<!\[CDATA\[/iu.test(xml), "DTD, entities, and CDATA are not supported.");
  const clean = xml.replace(/<!--[\s\S]*?-->/gu, "");
  assert(!clean.includes("<!--"), "Unclosed XML comment.");
  const versions = [];
  const stack = [];

  for (const token of clean.matchAll(/<(?:[^"'<>]|"[^"]*"|'[^']*')*>/gu)) {
    if (token[0].startsWith("<?")) continue;
    const close = /^<\/([A-Za-z_][\w:.-]*)\s*>$/u.exec(token[0]);

    if (close !== null) {
      const element = stack.pop();
      assert(element !== undefined && element.name === close[1], "Unbalanced XML elements.");
      if (element.name === "Version") {
        const value = clean.slice(element.contentStart, token.index).trim();
        assert(!value.includes("<") && isVersion(value), "Version must be a literal X.Y.Z.");
        versions.push(value);
      }
      continue;
    }

    const open = /^<([A-Za-z_][\w:.-]*)([\s\S]*?)(\/?)>$/u.exec(token[0]);
    assert(open !== null, "Unsupported XML declaration.");
    if (open[1] === "Version") {
      assert(open[2].trim() === "" && open[3] === "", "Version must be unconditional.");
      assert(
        stack.length === 2 &&
          stack[0].name === "Project" &&
          stack[1].name === "PropertyGroup" &&
          stack[1].attributes.trim() === "",
        "Version must be a direct child of an unconditional PropertyGroup.",
      );
    }
    if (open[3] === "") {
      stack.push({ name: open[1], attributes: open[2], contentStart: token.index + token[0].length });
    }
  }

  assert.equal(stack.length, 0, "Unclosed XML elements.");
  assert.equal(versions.length, 1, "Expected one unconditional literal Version declaration.");
  const version = versions[0];
  assert(isVersion(version), "Version must be a literal X.Y.Z.");
  return version;
};
