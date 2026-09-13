import js from "@eslint/js";
import eslintConfigPrettier from "eslint-config-prettier";
import jsxA11y from "eslint-plugin-jsx-a11y-x";
import reactHooks from "eslint-plugin-react-hooks";
import globals from "globals";
import tseslint from "typescript-eslint";

const baseRules = {
  complexity: ["error", 12],
  "max-lines": ["error", { max: 300, skipBlankLines: false, skipComments: false }],
  "max-lines-per-function": [
    "error",
    { max: 50, skipBlankLines: false, skipComments: false, IIFEs: true },
  ],
  "no-console": "error",
  "no-restricted-syntax": [
    "error",
    {
      selector: "JSXAttribute[name.name='dangerouslySetInnerHTML']",
      message: "Claimant content must render as text.",
    },
  ],
};

const typescriptRules = {
  "@typescript-eslint/no-floating-promises": "error",
  "@typescript-eslint/no-misused-promises": "error",
  "@typescript-eslint/no-unsafe-argument": "error",
  "@typescript-eslint/no-unsafe-assignment": "error",
  "@typescript-eslint/no-unsafe-call": "error",
  "@typescript-eslint/no-unsafe-member-access": "error",
  "@typescript-eslint/no-unsafe-return": "error",
};

const sourceRules = { ...baseRules, ...typescriptRules };

export default tseslint.config(
  // rationale: AJV/Rolldown output is bounded generated code; suppression-registry: web/eslint.config.mjs|eslint-config-ignore|line:39
  { ignores: ["src/generated/convergence/web-v2.validators.mjs"] },
  js.configs.recommended,
  ...tseslint.configs.recommendedTypeChecked.map((config) => ({
    ...config,
    files: ["**/*.{ts,tsx}"],
  })),
  {
    files: ["**/*.{ts,tsx}"],
    languageOptions: {
      globals: { ...globals.browser, ...globals.node },
      parserOptions: { projectService: true },
    },
    plugins: { "jsx-a11y-x": jsxA11y, "react-hooks": reactHooks },
    rules: {
      ...sourceRules,
      ...jsxA11y.configs.recommended.rules,
      ...reactHooks.configs.recommended.rules,
    },
  },
  {
    files: ["tests/**/*.{ts,tsx}", "e2e/**/*.{ts,tsx}"],
    languageOptions: { globals: { ...globals.browser, ...globals.node, ...globals.vitest } },
  },
  {
    files: ["src/{components,hooks,utils,views}/**/*.{ts,tsx}"],
    rules: {
      "no-restricted-globals": [
        "error",
        { name: "fetch", message: "Features must use the validated browser API boundary." },
      ],
    },
  },
  {
    files: ["scripts/**/*.mjs"],
    languageOptions: { globals: globals.node },
    rules: baseRules,
  },
  eslintConfigPrettier,
);
