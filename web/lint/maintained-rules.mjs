// Reuse ESLint's maintained selector rule through oxlint's JavaScript plugin API.
import { builtinRules } from "eslint/use-at-your-own-risk";

export default {
  meta: { name: "eslint-selectors" },
  rules: { "no-restricted-syntax": builtinRules.get("no-restricted-syntax") },
};
