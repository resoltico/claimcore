import ts from "typescript";

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
  const source = ts.createSourceFile("recovery.ts", recoveryTypes, ts.ScriptTarget.Latest, true);
  const owners = new Set([
    "PreparationSummary",
    "PreparationDetails",
    "RecoveryImportPreview",
    "RecoveryDetails",
  ]);
  const members = new Set(["authority", "settlement", "artifactKind", "tag"]);
  const visit = (node) => {
    if (ts.isPropertySignature(node) && members.has(node.name.getText(source))) {
      const type = node.type;
      const variants = ts.isUnionTypeNode(type) ? type.types : [type];
      for (const variant of variants)
        if (ts.isLiteralTypeNode(variant) && ts.isStringLiteral(variant.literal))
          result.add(variant.literal.text);
    }
    ts.forEachChild(node, visit);
  };
  for (const statement of source.statements)
    if (ts.isTypeAliasDeclaration(statement) && owners.has(statement.name.text))
      visit(statement.type);
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
