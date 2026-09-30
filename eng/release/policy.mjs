import assert from "node:assert/strict";

/** @param {unknown} value @returns {value is string} */
export const isVersion = (value) =>
  typeof value === "string" &&
  value === value.trim() &&
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/u.test(value);

/** @param {unknown} value @returns {value is string} */
export const isSha = (value) =>
  typeof value === "string" && value.length === 40 && /^[a-f0-9]+$/u.test(value);

/** Only transport newlines and trailing empty lines are normalized. */
/** @param {unknown} text @returns {string} */
export const canonicalNotes = (text) => {
  assert(typeof text === "string", "Release notes must be text.");
  const normalized = text.replaceAll("\r\n", "\n");
  assert(!normalized.includes("\r"), "Bare CR characters are not supported.");
  return normalized.replace(/\n+$/u, "");
};

const heading = /^ {0,3}##(?:[ \t]|$)/u;

/**
 * Whether `line` closes the open code fence.
 * @param {string} line
 * @param {string} fence The opening marker.
 */
const closesFence = (line, fence) => {
  const marker = /^ {0,3}(`+|~+)[ \t]*$/u.exec(line)?.[1];
  return marker !== undefined && marker[0] === fence[0] && marker.length >= fence.length;
};

/**
 * The marker of a code fence that `line` opens, or null.
 * @param {string} line
 * @returns {string | null}
 */
const opensFence = (line) => {
  const open = /^ {0,3}(`{3,}|~{3,})(.*)$/u.exec(line);
  if (open === null) return null;
  const marker = open[1] ?? "";
  assert(marker[0] !== "`" || !(open[2] ?? "").includes("`"), "Malformed code fence.");
  return marker;
};

/** @param {string[]} lines @returns {{ index: number, line: string }[]} */
const headingsOutsideFences = (lines) => {
  /** @type {{ index: number, line: string }[]} */
  const headings = [];
  /** @type {string | null} */
  let fence = null;

  for (const [index, line] of lines.entries()) {
    if (fence !== null) {
      if (closesFence(line, fence)) fence = null;
      continue;
    }
    fence = opensFence(line);
    if (fence !== null) continue;
    assert(!line.includes("<!--"), "HTML comments outside fenced code are not supported.");
    if (heading.test(line)) headings.push({ index, line });
  }

  assert.equal(fence, null, "Unclosed code fence in CHANGELOG.md.");
  return headings;
};

/** @param {string} text */
const labels = (text) =>
  [...text.matchAll(/\[([^\]\n]+)\]/gu)].map((match) =>
    (match[1] ?? "").trim().replace(/\s+/gu, " ").toLowerCase(),
  );

/** @param {string} text */
const definitions = (text) =>
  [...text.matchAll(/^ {0,3}\[([^\]\n]+)\]:/gmu)].map((match) =>
    (match[1] ?? "").trim().replace(/\s+/gu, " ").toLowerCase(),
  );

/** @param {string} changelog @param {string} section */
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
 * @param {string} changelog
 * @param {string} version
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

  const selected = /** @type {{ index: number, line: string }} */ (candidates[0]);
  const date = /^ {0,3}## \[[^\]]+\] - (\d{4}-\d{2}-\d{2})$/u.exec(selected.line)?.[1];
  assert(date !== undefined, "Expected: ## [X.Y.Z] - YYYY-MM-DD");
  const parsed = new Date(`${date}T00:00:00Z`);
  assert(
    !Number.isNaN(parsed.valueOf()) && parsed.toISOString().slice(0, 10) === date,
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

/**
 * @typedef {object} OpenElement
 * @property {string} name
 * @property {string} attributes
 * @property {number} contentStart Offset just past the opening tag.
 */

/**
 * Close the innermost element; a closing `Version` yields its literal value.
 * @param {string} clean
 * @param {OpenElement[]} stack
 * @param {string} name The closing tag's name.
 * @param {number} tokenStart
 * @returns {string | null}
 */
function closeElement(clean, stack, name, tokenStart) {
  const element = stack.pop();
  assert(element !== undefined && element.name === name, "Unbalanced XML elements.");
  if (element.name !== "Version") return null;
  const value = clean.slice(element.contentStart, tokenStart).trim();
  assert(!value.includes("<") && isVersion(value), "Version must be a literal X.Y.Z.");
  return value;
}

/**
 * Check where a `Version` element may appear.
 * @param {OpenElement[]} stack The enclosing elements.
 * @param {string} attributes
 * @param {string} selfClosing
 */
function assertVersionPlacement(stack, attributes, selfClosing) {
  assert(attributes.trim() === "" && selfClosing === "", "Version must be unconditional.");
  assert(
    stack.length === 2 &&
      stack[0]?.name === "Project" &&
      stack[1]?.name === "PropertyGroup" &&
      stack[1].attributes.trim() === "",
    "Version must be a direct child of an unconditional PropertyGroup.",
  );
}

/** Reads the one literal, unconditional Version directly owned by Directory.Build.props.
 * @param {string} xml
 */
export const declaredVersion = (xml) => {
  assert(typeof xml === "string", "Directory.Build.props must be text.");
  assert(
    !/<!DOCTYPE|<!ENTITY|<!\[CDATA\[/iu.test(xml),
    "DTD, entities, and CDATA are not supported.",
  );
  const clean = xml.replace(/<!--[\s\S]*?-->/gu, "");
  assert(!clean.includes("<!--"), "Unclosed XML comment.");
  /** @type {string[]} */
  const versions = [];
  /** @type {OpenElement[]} */
  const stack = [];

  for (const token of clean.matchAll(/<(?:[^"'<>]|"[^"]*"|'[^']*')*>/gu)) {
    const text = token[0];
    if (text.startsWith("<?")) continue;
    const close = /^<\/([A-Za-z_][\w:.-]*)\s*>$/u.exec(text);
    if (close !== null) {
      const value = closeElement(clean, stack, close[1] ?? "", token.index);
      if (value !== null) versions.push(value);
      continue;
    }
    const open = /^<([A-Za-z_][\w:.-]*)([\s\S]*?)(\/?)>$/u.exec(text);
    assert(open !== null, "Unsupported XML declaration.");
    const [, name = "", attributes = "", selfClosing = ""] = open;
    if (name === "Version") assertVersionPlacement(stack, attributes, selfClosing);
    if (selfClosing === "")
      stack.push({ name, attributes, contentStart: token.index + text.length });
  }

  assert.equal(stack.length, 0, "Unclosed XML elements.");
  assert.equal(versions.length, 1, "Expected one unconditional literal Version declaration.");
  const version = versions[0];
  assert(isVersion(version), "Version must be a literal X.Y.Z.");
  return version;
};
