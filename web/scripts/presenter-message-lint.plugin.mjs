const presenterMethods = new Set(["text", "integer", "term", "fieldLabel"]);
const passThroughExpressions = new Set([
  "ChainExpression",
  "ConditionalExpression",
  "LogicalExpression",
]);

/** @param {Extract<import("eslint").Rule.Node, { type: "CallExpression" }>["callee"]} callee */
const isPresenter = (callee) => {
  if (callee.type === "Identifier") {
    return callee.name === "renderCatalogKey";
  }
  return (
    callee.type === "MemberExpression" &&
    callee.object.type === "Identifier" &&
    callee.object.name === "p" &&
    callee.property.type === "Identifier" &&
    presenterMethods.has(callee.property.name)
  );
};

/** @type {import("eslint").Rule.RuleModule} */
const wholeMessages = {
  create(context) {
    return {
      CallExpression(node) {
        if (!isPresenter(node.callee)) {
          return;
        }
        const parent = context.sourceCode
          .getAncestors(node)
          .findLast((ancestor) => !passThroughExpressions.has(ancestor.type));
        if (
          parent?.type === "TemplateLiteral" ||
          (parent?.type === "BinaryExpression" && parent.operator === "+")
        ) {
          context.report({
            node,
            message: "Compose presenter output in a catalog message with named arguments.",
          });
        }
      },
    };
  },
};
export default { rules: { "whole-messages": wholeMessages } };
