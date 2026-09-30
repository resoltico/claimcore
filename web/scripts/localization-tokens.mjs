import { parseSync } from "oxc-parser";

const owners = new Set([
  "PreparationSummary",
  "PreparationDetails",
  "RecoveryImportPreview",
  "RecoveryDetails",
]);
const members = new Set(["authority", "settlement", "artifactKind", "tag"]);
const memberName = (key) => (key.type === "Identifier" ? key.name : key.value);

// The string literals a rendered member's type admits, whether one literal or a union of them.
const literalsOf = (signature) => {
  const type = signature.typeAnnotation?.typeAnnotation;
  const variants = type?.type === "TSUnionType" ? type.types : [type];
  return variants
    .filter(
      (variant) => variant?.type === "TSLiteralType" && typeof variant.literal.value === "string",
    )
    .map((variant) => variant.literal.value);
};

const visit = (node, result) => {
  if (node === null || typeof node !== "object") return;
  if (Array.isArray(node)) {
    for (const item of node) visit(item, result);
    return;
  }
  if (node.type === "TSPropertySignature" && members.has(memberName(node.key)))
    for (const literal of literalsOf(node)) result.add(literal);
  for (const child of Object.values(node)) visit(child, result);
};

const recoveryFamilies = (recoveryTypes, result) => {
  const { program, errors } = parseSync("recovery.ts", recoveryTypes);
  if (errors.length > 0) throw new Error("The recovery type declarations could not be parsed.");
  for (const statement of program.body) {
    const declaration =
      statement.type === "ExportNamedDeclaration" ? statement.declaration : statement;
    if (declaration?.type === "TSTypeAliasDeclaration" && owners.has(declaration.id.name))
      visit(declaration.typeAnnotation, result);
  }
};

// These are the generated type families actually rendered by this interface, not a parallel
// business-state registry. Changes in their literal vocabulary require corresponding catalogs.
export const renderedTokens = (semantic, recoveryTypes) => {
  const result = new Set();
  for (const field of semantic.fields)
    if (field.scalar.kind === "CASE_STATUS")
      for (const value of field.scalar.allowedValues) result.add(value);
  for (const command of semantic.commands)
    for (const group of command.inputs.groups ?? [])
      for (const action of group.actions) result.add(action);
  recoveryFamilies(recoveryTypes, result);
  if (result.size < 10) throw new Error("Rendered token families were not inspected.");
  return [...result].sort();
};

export const validateTokens = (catalog, expected) => {
  const actual = Object.keys(catalog)
    .filter((key) => key.startsWith("token."))
    .map((key) => key.slice(6))
    .sort();
  if (JSON.stringify(actual) !== JSON.stringify(expected))
    throw new Error("Rendered token catalogs differ from the generated contract families.");
};
