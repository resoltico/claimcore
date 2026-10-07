import { parse, TYPE } from "@formatjs/icu-messageformat-parser";
import { parseDocument } from "yaml";

/** @typedef {string | ({literal: string} | {hole: string})[]} Message */

/** @param {string} source @returns {Record<string, string>} */
export const readCatalog = (source) => {
  const parsed = parseDocument(source, { schema: "json", uniqueKeys: true });
  if (parsed.errors.length || parsed.warnings.length) {
    throw new Error("Catalog JSON is ambiguous or duplicated.");
  }
  const value = JSON.parse(source);
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("Catalog must be an object.");
  }
  return value;
};
/** @param {Record<string, Message>} catalog @param {Record<string, Message>} entries @param {string} domain */
export const addDomain = (catalog, entries, domain) => {
  for (const [key, value] of Object.entries(entries)) {
    if (!key.startsWith(`${domain}.`) || Object.hasOwn(catalog, key)) {
      throw new Error("Catalog domain ownership or key uniqueness failed.");
    }
    catalog[key] = value;
  }
};
const forbidden = /[<>\u202a-\u202e\u2066-\u2069\u200e\u200f]/u;
/** @param {import("@formatjs/icu-messageformat-parser").PluralElement} node @param {string} language */
const pluralSelector = (node, language) => {
  const categories = new Intl.PluralRules(language, { type: node.pluralType }).resolvedOptions()
    .pluralCategories;
  if (categories.some((name) => !Object.hasOwn(node.options, name))) {
    throw new Error("Incomplete plural categories.");
  }
  return [
    node.value,
    node.pluralType,
    node.offset,
    Object.keys(node.options)
      .filter((k) => k.startsWith("="))
      .sort(),
  ];
};
/** @param {string} name */
const assertArgumentName = (name) => {
  if (
    !/^[a-zA-Z][a-zA-Z0-9]*$/u.test(name) ||
    ["constructor", "prototype", "toString", "valueOf"].includes(name)
  ) {
    throw new Error("Unsafe interpolation argument name.");
  }
};
/** @param {import("@formatjs/icu-messageformat-parser").MessageFormatElement} node @returns {node is import("@formatjs/icu-messageformat-parser").ArgumentElement | import("@formatjs/icu-messageformat-parser").PluralElement | import("@formatjs/icu-messageformat-parser").SelectElement} */
const coordinatedArgument = (node) =>
  node.type === TYPE.argument || node.type === TYPE.plural || node.type === TYPE.select;

/** @param {import("@formatjs/icu-messageformat-parser").MessageFormatElement[]} nodes @param {string} language @param {Record<string, string>} args @param {unknown[]} selectors */
const inspectNodes = (nodes, language, args, selectors) => {
  for (const node of nodes) {
    if ([TYPE.number, TYPE.date, TYPE.time, TYPE.tag].includes(node.type)) {
      throw new Error("Catalog cannot format raw numbers/dates or markup.");
    }
    if (node.type === TYPE.literal && forbidden.test(node.value)) {
      throw new Error("Unsafe catalog literal.");
    }
    if (!coordinatedArgument(node)) {
      continue;
    }
    assertArgumentName(node.value);
    const role = node.type === TYPE.plural ? "number" : "string";
    if (args[node.value] && args[node.value] !== role) {
      throw new Error("Inconsistent argument role.");
    }
    args[node.value] = role;
    if (node.type === TYPE.argument) {
      continue;
    }
    selectors.push(
      node.type === TYPE.plural
        ? pluralSelector(node, language)
        : [node.value, Object.keys(node.options).sort()],
    );
    for (const option of Object.values(node.options)) {
      inspectNodes(option.value, language, args, selectors);
    }
  }
};
/** @param {string} message */
const parseCatalogText = (message) => {
  if (message.trim() === "" || message.length > 8000) {
    throw new Error("Invalid catalog text.");
  }
  return parse(message, { requiresOtherClause: true });
};
/** @param {Exclude<Message, string> | undefined} parts @returns {import("@formatjs/icu-messageformat-parser").MessageFormatElement[]} */
const literalParts = (parts) => {
  if (!Array.isArray(parts) || parts.length === 0 || parts.length > 8000) {
    throw new Error("Invalid presentation parts.");
  }
  let length = 0;
  const ast = parts.map(
    /** @returns {import("@formatjs/icu-messageformat-parser").LiteralElement | import("@formatjs/icu-messageformat-parser").ArgumentElement} */
    (part) => {
      if (!part || typeof part !== "object" || Object.keys(part).length !== 1) {
        throw new Error("Invalid presentation part.");
      }
      const literal = "literal" in part;
      const value = literal ? part.literal : part.hole;
      if (typeof value !== "string") {
        throw new Error("Invalid presentation part value.");
      }
      length += value.length + (literal ? 0 : 2);
      return literal ? { type: TYPE.literal, value } : { type: TYPE.argument, value };
    },
  );
  if (length === 0 || length > 8000 || ast.every((node) => node.value.trim() === "")) {
    throw new Error("Invalid complete presentation message.");
  }
  return ast;
};
/** @param {Message | undefined} message @param {string} language */
export const messageShape = (message, language) => {
  const ast = typeof message === "string" ? parseCatalogText(message) : literalParts(message);
  /** @type {Record<string, string>} */
  const args = {};
  /** @type {unknown[]} */
  const selectors = [];
  inspectNodes(ast, language, args, selectors);
  return {
    ast,
    args: Object.fromEntries(Object.entries(args).sort()),
    selectors: [...new Set(selectors.map((value) => JSON.stringify(value)))].sort(),
  };
};
/** @param {Record<string, Message>} source @param {Record<string, Message>} translated @param {string} language */
export const validateCatalog = (source, translated, language) => {
  if (
    JSON.stringify(Object.keys(source).sort()) !== JSON.stringify(Object.keys(translated).sort())
  ) {
    throw new Error("Catalog keys differ.");
  }
  for (const key of Object.keys(source)) {
    const a = messageShape(source[key], "en");
    const b = messageShape(translated[key], language);
    if (
      JSON.stringify(a.args) !== JSON.stringify(b.args) ||
      JSON.stringify(a.selectors) !== JSON.stringify(b.selectors)
    ) {
      throw new Error(`Catalog argument contract differs: ${key}`);
    }
  }
};
/** @param {string} value */
const expand = (value) =>
  value.replace(
    /[aeiouAEIOU]/gu,
    (c) =>
      ({
        a: "àà",
        e: "ëë",
        i: "ïï",
        o: "öö",
        u: "üü",
        A: "ÀÀ",
        E: "ËË",
        I: "ÏÏ",
        O: "ÖÖ",
        U: "ÜÜ",
      })[/** @type {"a"|"e"|"i"|"o"|"u"|"A"|"E"|"I"|"O"|"U"} */ (c)] ?? c,
  );
/** @param {import("@formatjs/icu-messageformat-parser").MessageFormatElement[]} ast @returns {import("@formatjs/icu-messageformat-parser").MessageFormatElement[]} */
export const pseudolocalize = (ast) =>
  ast.map((node) => {
    if (node.type === TYPE.literal) {
      return { ...node, value: expand(node.value) };
    }
    if (node.type === TYPE.plural || node.type === TYPE.select) {
      return {
        ...node,
        options: Object.fromEntries(
          Object.entries(node.options).map(([k, v]) => [
            k,
            { ...v, value: pseudolocalize(v.value) },
          ]),
        ),
      };
    }
    return structuredClone(node);
  });
/** @param {import("./tooling-types.mjs").JsonRecord} semantic @param {import("./tooling-types.mjs").JsonRecord} hostSchema @returns {Record<string, Record<string, unknown>>} */
export const diagnosticRequirements = (semantic, hostSchema) => {
  const result = Object.fromEntries(
    [
      semantic["rejectionDiagnostics"],
      semantic["faultDiagnostics"],
      semantic["recoveryDiagnostics"],
    ]
      .flat()
      .map((d) => [
        d.id,
        Object.fromEntries(
          d.parameters.map((/** @type {{name: string, minimum: number, maximum: number}} */ p) => [
            p.name,
            { minimum: p.minimum, maximum: p.maximum },
          ]),
        ),
      ]),
  );
  /** @param {import("./tooling-types.mjs").JsonRecord} node */
  const visit = (node) => {
    if (!node || typeof node !== "object") {
      return;
    }
    const d = node["properties"]?.diagnostic?.properties;
    if (d) {
      for (const id of d.id.enum ?? [d.id.const]) {
        result[id] = d.parameters.properties;
      }
    }
    for (const value of Object.values(node)) {
      if (Array.isArray(value)) {
        value.forEach(visit);
      } else {
        visit(value);
      }
    }
  };
  visit(hostSchema);
  return result;
};
/** @param {Message | undefined} message @param {Record<string, unknown>} args @param {string} id */
const assertDiagnosticShape = (message, args, id) => {
  const actual = messageShape(message, "en").args;
  if (
    JSON.stringify(Object.keys(actual).sort()) !== JSON.stringify(Object.keys(args).sort()) ||
    Object.values(actual).some((r) => r !== "string")
  ) {
    // Constraint limits are preformatted exact strings, never ICU number/currency values.
    throw new Error(`Diagnostic parameters differ: ${id}`);
  }
};
/** @param {Record<string, Message>} catalog @param {import("./tooling-types.mjs").JsonRecord} semantic @param {Record<string, Record<string, unknown>>} requirements */
export const validateCoverage = (catalog, semantic, requirements) => {
  /** @type {string[]} */
  const keys = [];
  for (const field of semantic["fields"]) {
    for (const s of ["label", "meaning"]) {
      keys.push(`field.${field.name}.${s}`);
    }
  }
  for (const c of semantic["commands"]) {
    for (const s of ["label", "meaning"]) {
      keys.push(`command.${c.kind}.${s}`);
    }
    for (const g of c.inputs.groups ?? []) {
      for (const s of ["label", "meaning"]) {
        keys.push(`group.${g.name}.${s}`);
      }
    }
  }
  for (const [id, args] of Object.entries(requirements)) {
    const key = `diagnostic.${id}`;
    keys.push(key);
    assertDiagnosticShape(catalog[key], args, id);
  }
  if (keys.some((k) => !Object.hasOwn(catalog, k))) {
    throw new Error("Catalog omits authoritative metadata.");
  }
  const owned = Object.keys(catalog).filter((k) => /^(field|command|group|diagnostic)\./u.test(k));
  if (owned.some((k) => !keys.includes(k))) {
    throw new Error("Catalog contains obsolete metadata.");
  }
};
