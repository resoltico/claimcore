// Boundary rules that no built-in oxlint rule expresses, run through oxlint's JavaScript plugin API.

/** @param {import("oxlint/plugins-dev").Expression} source */
function importedText(source) {
  if (source.type === "Literal" && typeof source.value === "string") return source.value;
  if (source.type === "TemplateLiteral")
    return source.quasis.map((part) => part.value.cooked ?? "").join("");
  return null;
}

/** @type {import("oxlint/plugins-dev").Rule} */
const noPresentationImport = {
  meta: {
    type: "problem",
    docs: {
      description:
        "Operation state and request coordination must not load presentation code dynamically.",
    },
    messages: {
      forbidden: "Do not load presentation from operation state or request coordination.",
    },
    schema: [],
  },
  create(context) {
    return {
      ImportExpression(node) {
        const text = importedText(node.source);
        if (text !== null && /presentation/.test(text))
          context.report({ node, messageId: "forbidden" });
      },
    };
  },
};

const plugin = {
  meta: { name: "claimcore" },
  rules: { "no-presentation-import": noPresentationImport },
};

export default plugin;
