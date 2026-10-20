import { parseSync } from "oxc-parser";

const owners = new Set([
  "CaseReceipt",
  "AuthorityReceipt",
  "CaseView",
  "CaseState_LIVE",
  "Principal",
]);
const renderedKindFamilies = ["Scope_", "AuthorityAct_"];
const members = new Set(["act", "disposition", "kind"]);
/** @param {import("./tooling-types.mjs").JsonRecord} key */
const memberName = (key) => (key["type"] === "Identifier" ? key["name"] : key["value"]);

// The string literals a rendered member's type admits, whether one literal or a union of them.
/** @param {import("./tooling-types.mjs").JsonRecord} signature @returns {string[]} */
const literalsOf = (signature) => {
  const type = signature["typeAnnotation"]?.typeAnnotation;
  const variants = type?.type === "TSUnionType" ? type.types : [type];
  return variants
    .filter(
      (/** @type {import("./tooling-types.mjs").JsonRecord} */ variant) =>
        variant?.["type"] === "TSLiteralType" && typeof variant["literal"].value === "string",
    )
    .map(
      (/** @type {import("./tooling-types.mjs").JsonRecord} */ variant) => variant["literal"].value,
    );
};

/** @param {import("./tooling-types.mjs").JsonRecord | null} node @param {Set<string>} result */
const visit = (node, result) => {
  if (node === null || typeof node !== "object") {
    return;
  }
  if (Array.isArray(node)) {
    for (const item of node) {
      visit(item, result);
    }
    return;
  }
  if (node["type"] === "TSPropertySignature" && members.has(memberName(node["key"]))) {
    for (const literal of literalsOf(node)) {
      result.add(literal);
    }
  }
  for (const child of Object.values(node)) {
    visit(child, result);
  }
};

/** @param {string} types @param {Set<string>} result */
const renderedFamilies = (types, result) => {
  const { program, errors } = parseSync("client.ts", types);
  if (errors.length > 0) {
    throw new Error("The rendered type declarations could not be parsed.");
  }
  for (const statement of program.body) {
    const declaration =
      statement.type === "ExportNamedDeclaration" ? statement.declaration : statement;
    if (
      declaration?.type === "TSTypeAliasDeclaration" &&
      (owners.has(declaration.id.name) ||
        renderedKindFamilies.some((prefix) => declaration.id.name.startsWith(prefix)))
    ) {
      visit(declaration.typeAnnotation, result);
    }
  }
};

// These are the generated type families actually rendered by this interface, not a parallel
// business-state registry. Changes in their literal vocabulary require corresponding catalogs.
/** @param {import("./tooling-types.mjs").JsonRecord} descriptors @param {string} types */
export const renderedTokens = (descriptors, types) => {
  const result = new Set();
  for (const field of descriptors["fields"]) {
    if (field.scalar.kind === "CASE_STATUS") {
      for (const value of field.scalar.allowedValues) {
        result.add(value);
      }
    }
  }
  for (const command of descriptors["commands"]) {
    for (const group of command.inputs.groups ?? []) {
      for (const action of group.actions) {
        result.add(action);
      }
    }
  }
  renderedFamilies(types, result);
  for (const entry of descriptors["roles"]) {
    result.add(entry.role);
  }
  result.add("ERASED");
  if (result.size < 7) {
    throw new Error("Rendered token families were not inspected.");
  }
  return [...result].sort();
};

/** @param {Record<string, import("./localization-policy.mjs").Message>} catalog @param {string[]} expected */
export const validateTokens = (catalog, expected) => {
  const actual = Object.keys(catalog)
    .filter((key) => key["startsWith"]("token."))
    .map((key) => key["slice"](6))
    .sort();
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error("Rendered token catalogs differ from the generated contract families.");
  }
};
