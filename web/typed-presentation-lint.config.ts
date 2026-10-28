import type { OxlintConfig } from "oxlint";

export const typedPresentationPolicy: NonNullable<OxlintConfig["overrides"]>[number] = {
  files: ["**/*.ts", "**/*.tsx"],
  rules: {
    "jsx-a11y/prefer-tag-over-role": "off",
    "react-hooks-js/config": "error",
    "react-hooks-js/error-boundaries": "error",
    "react-hooks-js/exhaustive-deps": "error",
    "react-hooks-js/gating": "error",
    "react-hooks-js/globals": "error",
    "react-hooks-js/immutability": "error",
    "react-hooks-js/incompatible-library": "error",
    "react-hooks-js/preserve-manual-memoization": "error",
    "react-hooks-js/purity": "error",
    "react-hooks-js/refs": "error",
    "react-hooks-js/rules-of-hooks": "error",
    "react-hooks-js/set-state-in-effect": "error",
    "react-hooks-js/set-state-in-render": "error",
    "react-hooks-js/static-components": "error",
    "react-hooks-js/unsupported-syntax": "error",
    "react-hooks-js/use-memo": "error",
    "react/no-danger": "error",
    "typescript/array-type": [
      "error",
      {
        default: "array",
        readonly: "generic",
      },
    ],
    "typescript/consistent-return": "off",
    "typescript/consistent-type-definitions": "off",
    "typescript/no-array-constructor": "error",
    "typescript/no-empty-object-type": "error",
    "typescript/no-explicit-any": "error",
    "typescript/no-namespace": "error",
    "typescript/no-require-imports": "error",
    "typescript/no-unused-expressions": "error",
    "typescript/no-unused-vars": "error",
    "typescript/prefer-readonly-parameter-types": "off",
    "typescript/switch-exhaustiveness-check": [
      "error",
      {
        considerDefaultExhaustiveForUnions: true,
      },
    ],
  },
  plugins: ["typescript"],
};
