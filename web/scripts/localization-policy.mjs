export { pseudolocalize } from "../src/presentation/pseudolocalization.mjs";
import { parse, TYPE } from "@formatjs/icu-messageformat-parser";
import { parseDocument } from "yaml";

/** @typedef {string} Message */

/** @param {unknown} value @returns {value is Record<string, unknown>} */
const isRecord = (value) => typeof value === "object" && value !== null && !Array.isArray(value);
/** @param {unknown} value @returns {value is Record<string, Message>} */
const isCatalog = (value) =>
  isRecord(value) && Object.values(value).every((message) => typeof message === "string");

/** @param {string} source @returns {Record<string, Message>} */
export const readCatalog = (source) => {
  const parsed = parseDocument(source, { schema: "json", uniqueKeys: true });
  if (parsed.errors.length || parsed.warnings.length) {
    throw new Error("Catalog JSON is ambiguous or duplicated.");
  }
  /** @type {unknown} */
  const value = JSON.parse(source);
  if (!isCatalog(value)) {
    throw new Error("Invalid catalog message shape.");
  }
  return value;
};
/** @param {Record<string, Message>} catalog @param {ReadonlyArray<string>} domains */
export const validatePrefixes = (catalog, domains) => {
  if (Object.keys(catalog).some((key) => !domains.some((domain) => key.startsWith(`${domain}.`)))) {
    throw new Error("Catalog contains an unknown message family.");
  }
};
const forbidden = /[<>\u202a-\u202e\u2066-\u2069\u200e\u200f]/u;
/** @param {string} text */
const unsafeText = (text) =>
  !text.isWellFormed() ||
  Array.from(text).some((character) => {
    const scalar = character.codePointAt(0) ?? 0;
    return (scalar >= 0xfdd0 && scalar <= 0xfdef) || (scalar & 0xfffe) === 0xfffe;
  }) ||
  forbidden.test(text) ||
  Array.from(text).some((character) => {
    const code = character.charCodeAt(0);
    return (code < 32 && code !== 9 && code !== 10) || (code >= 127 && code <= 159);
  });
/** @param {string} name */
const assertArgumentName = (name) => {
  if (
    !/^[a-zA-Z][a-zA-Z0-9]*$/u.test(name) ||
    ["constructor", "prototype", "toString", "valueOf"].includes(name)
  ) {
    throw new Error("Unsafe interpolation argument name.");
  }
};
/** @param {import("@formatjs/icu-messageformat-parser").MessageFormatElement[]} nodes @param {Record<string, string>} args */
const inspectNodes = (nodes, args) => {
  for (const node of nodes) {
    if (node.type === TYPE.literal) {
      if (unsafeText(node.value)) {
        throw new Error("Unsafe catalog literal.");
      }
    } else if (node.type === TYPE.argument) {
      assertArgumentName(node.value);
      args[node.value] = "string";
    } else {
      throw new Error("Catalog supports literal text and simple named arguments only.");
    }
  }
};
/** @param {string} message */
const assertBraceSyntax = (message) => {
  let quoted = false;
  let depth = 0;
  for (let index = 0; index < message.length; index++) {
    const character = message[index];
    if (character === "'" && message[index + 1] === "'") {
      index++;
    } else if (character === "'" && (quoted || ["{", "}"].includes(message.charAt(index + 1)))) {
      quoted = !quoted;
    } else if (!quoted && character === "{") {
      depth++;
    } else if (!quoted && character === "}" && --depth < 0) {
      throw new Error("Unexpected closing message brace.");
    }
  }
};
/** @param {string} message */
const parseCatalogText = (message) => {
  if (message.trim() === "" || message.length > 8000 || unsafeText(message)) {
    throw new Error("Invalid catalog text.");
  }
  assertBraceSyntax(message);
  return parse(message, { requiresOtherClause: true });
};
/** @param {Message | undefined} message @param {string} _language */
export const messageShape = (message, _language) => {
  if (typeof message !== "string") {
    throw new Error("Catalog messages must be ICU strings.");
  }
  const ast = parseCatalogText(message);
  /** @type {Record<string, string>} */
  const args = {};
  inspectNodes(ast, args);
  return {
    ast,
    args: Object.fromEntries(Object.entries(args).sort()),
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
    if (JSON.stringify(a.args) !== JSON.stringify(b.args)) {
      throw new Error(`Catalog argument contract differs: ${key}`);
    }
  }
};
/** @param {import("./tooling-types.mjs").JsonRecord} descriptors @param {import("./tooling-types.mjs").JsonRecord} hostSchema @returns {Record<string, Record<string, unknown>>} */
export const diagnosticRequirements = (descriptors, hostSchema) =>
  Object.fromEntries([
    ...descriptors["rejectionDiagnostics"].map(
      (/** @type {import("./tooling-types.mjs").JsonRecord} */ d) => [
        `diagnostic.${d["id"]}`,
        Object.fromEntries(
          d["parameters"].map((/** @type {import("./tooling-types.mjs").JsonRecord} */ p) => [
            p["name"],
            { minimum: p["minimum"], maximum: p["maximum"] },
          ]),
        ),
      ],
    ),
    ...descriptors["faultDiagnostics"].map(
      (/** @type {import("./tooling-types.mjs").JsonRecord} */ d) => [`fault.${d["id"]}`, {}],
    ),
    ...hostSchema["components"].schemas.HostFailure.oneOf.map(
      (/** @type {import("./tooling-types.mjs").JsonRecord} */ branch) => [
        `host.${branch["properties"].code.const}`,
        {},
      ],
    ),
  ]);
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
/** @param {import("./tooling-types.mjs").JsonRecord} descriptors */
const authorityKeys = (descriptors) => {
  /** @type {Array<{kind:string}>} */
  const commands = descriptors["authorityCommands"] ?? [];
  /** @type {Array<{labelId:string}>} */
  const fields = descriptors["authorityFields"] ?? [];
  return [
    ...fields.map((field) => field.labelId),
    ...commands.flatMap((command) =>
      ["label", "meaning"].map((part) => `authority.${command.kind}.${part}`),
    ),
  ];
};
/** @param {Record<string, Message>} catalog @param {import("./tooling-types.mjs").JsonRecord} descriptors @param {Record<string, Record<string, unknown>>} requirements */
export const validateCoverage = (catalog, descriptors, requirements) => {
  /** @type {string[]} */
  const keys = [];
  for (const field of [...descriptors["fields"], ...(descriptors["inputFields"] ?? [])]) {
    for (const s of ["label", "meaning"]) {
      keys.push(`field.${field.name}.${s}`);
    }
  }
  for (const c of descriptors["commands"]) {
    for (const s of ["label", "meaning"]) {
      keys.push(`command.${c.kind}.${s}`);
    }
    for (const g of c.inputs.groups ?? []) {
      for (const s of ["label", "meaning"]) {
        keys.push(`group.${g.name}.${s}`);
      }
    }
  }
  keys.push(...authorityKeys(descriptors));
  for (const [id, args] of Object.entries(requirements)) {
    const key = id;
    keys.push(key);
    assertDiagnosticShape(catalog[key], args, id);
  }
  if (keys.some((k) => !Object.hasOwn(catalog, k))) {
    throw new Error("Catalog omits authoritative metadata.");
  }
  const owned = Object.keys(catalog).filter((k) =>
    /^(field|command|group|authority|diagnostic|fault|host)\./u.test(k),
  );
  if (owned.some((k) => !keys.includes(k))) {
    throw new Error("Catalog contains obsolete metadata.");
  }
};
