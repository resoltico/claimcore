import { parse, TYPE } from "@formatjs/icu-messageformat-parser";
import { parseDocument } from "yaml";

export const readCatalog = (source) => {
  const parsed = parseDocument(source, { schema: "json", uniqueKeys: true });
  if (parsed.errors.length || parsed.warnings.length)
    throw new Error("Catalog JSON is ambiguous or duplicated.");
  const value = JSON.parse(source);
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new Error("Catalog must be an object.");
  return value;
};
export const addDomain = (catalog, entries, domain) => {
  for (const [key, value] of Object.entries(entries)) {
    if (!key.startsWith(`${domain}.`) || Object.hasOwn(catalog, key))
      throw new Error("Catalog domain ownership or key uniqueness failed.");
    catalog[key] = value;
  }
};
const forbidden = /[<>\u202a-\u202e\u2066-\u2069\u200e\u200f]/u;
const pluralSelector = (node, language) => {
  const categories = new Intl.PluralRules(language, { type: node.pluralType }).resolvedOptions()
    .pluralCategories;
  if (categories.some((name) => !Object.hasOwn(node.options, name)))
    throw new Error("Incomplete plural categories.");
  return [
    node.value,
    node.pluralType,
    node.offset,
    Object.keys(node.options)
      .filter((k) => k.startsWith("="))
      .sort(),
  ];
};
const assertArgumentName = (name) => {
  if (
    !/^[a-zA-Z][a-zA-Z0-9]*$/u.test(name) ||
    ["constructor", "prototype", "toString", "valueOf"].includes(name)
  )
    throw new Error("Unsafe interpolation argument name.");
};
const inspectNodes = (nodes, language, args, selectors) => {
  for (const node of nodes) {
    if ([TYPE.number, TYPE.date, TYPE.time, TYPE.tag].includes(node.type))
      throw new Error("Catalog cannot format raw numbers/dates or markup.");
    if (node.type === TYPE.literal && forbidden.test(node.value))
      throw new Error("Unsafe catalog literal.");
    if (![TYPE.argument, TYPE.plural, TYPE.select].includes(node.type)) continue;
    assertArgumentName(node.value);
    const role = node.type === TYPE.plural ? "number" : "string";
    if (args[node.value] && args[node.value] !== role)
      throw new Error("Inconsistent argument role.");
    args[node.value] = role;
    if (node.type === TYPE.argument) continue;
    selectors.push(
      node.type === TYPE.plural
        ? pluralSelector(node, language)
        : [node.value, Object.keys(node.options).sort()],
    );
    for (const option of Object.values(node.options))
      inspectNodes(option.value, language, args, selectors);
  }
};
export const messageShape = (message, language) => {
  if (typeof message !== "string" || message.trim() === "" || message.length > 8000)
    throw new Error("Invalid catalog text.");
  const ast = parse(message, { requiresOtherClause: true });
  const args = {};
  const selectors = [];
  inspectNodes(ast, language, args, selectors);
  return {
    ast,
    args: Object.fromEntries(Object.entries(args).sort()),
    selectors: [...new Set(selectors.map((value) => JSON.stringify(value)))].sort(),
  };
};
export const validateCatalog = (source, translated, language) => {
  if (JSON.stringify(Object.keys(source).sort()) !== JSON.stringify(Object.keys(translated).sort()))
    throw new Error("Catalog keys differ.");
  for (const key of Object.keys(source)) {
    const a = messageShape(source[key], "en"),
      b = messageShape(translated[key], language);
    if (
      JSON.stringify(a.args) !== JSON.stringify(b.args) ||
      JSON.stringify(a.selectors) !== JSON.stringify(b.selectors)
    )
      throw new Error(`Catalog argument contract differs: ${key}`);
  }
};
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
      })[c],
  );
export const pseudolocalize = (ast) =>
  ast.map((node) => {
    if (node.type === TYPE.literal) return { ...node, value: expand(node.value) };
    if (node.type === TYPE.plural || node.type === TYPE.select)
      return {
        ...node,
        options: Object.fromEntries(
          Object.entries(node.options).map(([k, v]) => [
            k,
            { ...v, value: pseudolocalize(v.value) },
          ]),
        ),
      };
    return structuredClone(node);
  });
export const diagnosticRequirements = (semantic, hostSchema) => {
  const result = Object.fromEntries(
    [semantic.rejectionDiagnostics, semantic.faultDiagnostics, semantic.recoveryDiagnostics]
      .flat()
      .map((d) => [
        d.id,
        Object.fromEntries(
          d.parameters.map((p) => [p.name, { minimum: p.minimum, maximum: p.maximum }]),
        ),
      ]),
  );
  const visit = (node) => {
    if (!node || typeof node !== "object") return;
    const d = node.properties?.diagnostic?.properties;
    if (d) for (const id of d.id.enum ?? [d.id.const]) result[id] = d.parameters.properties;
    for (const value of Object.values(node))
      if (Array.isArray(value)) value.forEach(visit);
      else visit(value);
  };
  visit(hostSchema);
  return result;
};
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
export const validateCoverage = (catalog, semantic, requirements) => {
  const keys = [];
  for (const field of semantic.fields)
    for (const s of ["label", "meaning"]) keys.push(`field.${field.name}.${s}`);
  for (const c of semantic.commands) {
    for (const s of ["label", "meaning"]) keys.push(`command.${c.kind}.${s}`);
    for (const g of c.inputs.groups ?? [])
      for (const s of ["label", "meaning"]) keys.push(`group.${g.name}.${s}`);
  }
  for (const [id, args] of Object.entries(requirements)) {
    const key = `diagnostic.${id}`;
    keys.push(key);
    assertDiagnosticShape(catalog[key], args, id);
  }
  if (keys.some((k) => !Object.hasOwn(catalog, k)))
    throw new Error("Catalog omits authoritative metadata.");
  const owned = Object.keys(catalog).filter((k) => /^(field|command|group|diagnostic)\./u.test(k));
  if (owned.some((k) => !keys.includes(k))) throw new Error("Catalog contains obsolete metadata.");
};
