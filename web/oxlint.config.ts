import { defineConfig, type OxlintConfig } from "oxlint";
import { fileURLToPath } from "node:url";
import { parse } from "node:path";

const checkout = fileURLToPath(new URL("../", import.meta.url));
const { root } = parse(checkout);
const allowed = (path: string) => fileURLToPath(new URL(path, import.meta.url)).slice(root.length);

const config: OxlintConfig = defineConfig({
  categories: {
    correctness: "error",
    suspicious: "error",
    pedantic: "error",
    perf: "error",
    style: "error",
  },
  env: {
    es2024: true,
  },
  plugins: ["eslint", "oxc", "react", "jsx-a11y", "import"],
  options: {
    denyWarnings: true,
    reportUnusedDisableDirectives: "error",
    typeAware: true,
  },
  rules: {
    "capitalized-comments": "off",
    complexity: [
      "error",
      {
        max: 12,
      },
    ],
    "for-direction": "error",
    "func-style": "off",
    "id-length": "off",
    "init-declarations": "off",
    "max-lines": "off",
    "max-lines-per-function": [
      "error",
      {
        max: 50,
        skipBlankLines: false,
        skipComments: false,
        IIFEs: true,
      },
    ],
    "max-params": [
      "error",
      {
        max: 5,
      },
    ],
    "max-statements": [
      "error",
      {
        max: 50,
      },
    ],
    "no-alert": "error",
    "no-async-promise-executor": "error",
    "no-case-declarations": "error",
    "no-compare-neg-zero": "error",
    "no-cond-assign": "error",
    "no-constant-binary-expression": "error",
    "no-constant-condition": "error",
    "no-continue": "off",
    "no-control-regex": "error",
    "no-debugger": "error",
    "no-delete-var": "error",
    "no-dupe-else-if": "error",
    "no-duplicate-case": "error",
    "no-empty": "error",
    "no-empty-character-class": "error",
    "no-empty-function": "error",
    "no-empty-pattern": "error",
    "no-empty-static-block": "error",
    "no-eq-null": "error",
    "no-eval": "error",
    "no-ex-assign": "error",
    "no-extra-boolean-cast": "error",
    "no-fallthrough": "error",
    "no-global-assign": "error",
    "no-inline-comments": "off",
    "no-invalid-regexp": "error",
    "no-irregular-whitespace": "error",
    "no-loss-of-precision": "error",
    "no-magic-numbers": "off",
    "no-misleading-character-class": "error",
    "no-nonoctal-decimal-escape": "error",
    "no-prototype-builtins": "error",
    "no-regex-spaces": "error",
    "no-self-assign": "error",
    "no-shadow-restricted-names": "error",
    "no-sparse-arrays": "error",
    "no-ternary": "off",
    "no-unassigned-vars": "error",
    "no-unsafe-finally": "error",
    "no-unsafe-optional-chaining": "error",
    "no-unused-labels": "error",
    "no-unused-private-class-members": "error",
    "no-use-before-define": [
      "error",
      {
        functions: false,
        classes: true,
        variables: true,
      },
    ],
    "no-useless-assignment": "error",
    "no-useless-backreference": "error",
    "no-useless-catch": "error",
    "no-useless-escape": "error",
    "no-var": "error",
    "one-var": ["error", "never"],
    "prefer-const": "error",
    "prefer-named-capture-group": "off",
    "prefer-rest-params": "error",
    "prefer-spread": "error",
    "preserve-caught-error": "error",
    "require-yield": "error",
    "sort-imports": "off",
    "sort-keys": "off",
    "use-isnan": "error",
    "valid-typeof": "error",
    "no-console": "error",
    "no-duplicate-imports": [
      "error",
      {
        allowSeparateTypeImports: true,
      },
    ],
    "react/function-component-definition": "off",
    "react/jsx-handler-names": "off",
    "react/jsx-max-depth": "off",
    "react/jsx-props-no-spreading": "off",
    "react/react-in-jsx-scope": "off",
    "import/no-cycle": "error",
    "no-restricted-imports": [
      "error",
      {
        patterns: [
          {
            group: ["**/api/transport", "**/api/transport.ts"],
            message: "Use the service facade (src/api/service.ts).",
          },
        ],
      },
    ],
    "import/consistent-type-specifier-style": "off",
    "import/default": "off",
    "import/exports-last": "off",
    "import/first": "off",
    "import/group-exports": "off",
    "import/max-dependencies": "off",
    "import/namespace": "off",
    "import/newline-after-import": "off",
    "import/no-absolute-path": "off",
    "import/no-anonymous-default-export": "off",
    "import/no-duplicates": "off",
    "import/no-empty-named-blocks": "off",
    "import/no-mutable-exports": "off",
    "import/no-named-as-default": "off",
    "import/no-named-as-default-member": "off",
    "import/no-named-default": "off",
    "import/no-named-export": "off",
    "import/no-namespace": "off",
    "import/no-nodejs-modules": "off",
    "import/no-self-import": "off",
    "import/no-unassigned-import": "off",
    "import/prefer-default-export": "off",
  },
  jsPlugins: [
    {
      name: "react-hooks-js",
      specifier: "eslint-plugin-react-hooks",
    },
    {
      name: "import-boundary",
      specifier: "eslint-plugin-import-x",
    },
  ],
  settings: {
    "import-x/resolver": {
      typescript: {
        project: "tsconfig.app.json",
      },
    },
  },
  overrides: [
    {
      files: [
        "src/**/*.ts",
        "src/**/*.tsx",
        "tests/**/*.ts",
        "tests/**/*.tsx",
        "e2e/**/*.ts",
        "e2e/**/*.tsx",
      ],
      env: {
        browser: true,
      },
    },
    {
      files: [
        "scripts/**/*.mjs",
        "*.config.ts",
        "*.config.mjs",
        "e2e/**/*.ts",
        "tests/**/*.ts",
        "tests/**/*.tsx",
      ],
      env: {
        node: true,
      },
    },
    {
      files: ["scripts/**/*.mjs", "*.config.ts", "*.config.mjs"],
      rules: {
        "no-restricted-globals": [
          "error",
          "window",
          "document",
          "navigator",
          "localStorage",
          "sessionStorage",
          "self",
        ],
        "no-undef": "error",
      },
    },
    {
      files: ["**/*.ts", "**/*.tsx"],
      rules: {
        "jsx-a11y/alt-text": "error",
        "jsx-a11y/anchor-has-content": "error",
        "jsx-a11y/anchor-is-valid": "error",
        "jsx-a11y/aria-activedescendant-has-tabindex": "error",
        "jsx-a11y/aria-props": "error",
        "jsx-a11y/aria-proptypes": "error",
        "jsx-a11y/aria-role": "error",
        "jsx-a11y/aria-unsupported-elements": "error",
        "jsx-a11y/autocomplete-valid": "error",
        "jsx-a11y/click-events-have-key-events": "error",
        "jsx-a11y/heading-has-content": "error",
        "jsx-a11y/html-has-lang": "error",
        "jsx-a11y/iframe-has-title": "error",
        "jsx-a11y/img-redundant-alt": "error",
        "jsx-a11y/interactive-supports-focus": "error",
        "jsx-a11y/label-has-associated-control": "error",
        "jsx-a11y/media-has-caption": "error",
        "jsx-a11y/mouse-events-have-key-events": "error",
        "jsx-a11y/no-access-key": "error",
        "jsx-a11y/no-autofocus": "error",
        "jsx-a11y/no-distracting-elements": "error",
        "jsx-a11y/no-interactive-element-to-noninteractive-role": "error",
        "jsx-a11y/no-noninteractive-element-interactions": "error",
        "jsx-a11y/no-noninteractive-element-to-interactive-role": "error",
        "jsx-a11y/no-noninteractive-tabindex": "error",
        "jsx-a11y/no-redundant-roles": "error",
        "jsx-a11y/no-static-element-interactions": "error",
        "jsx-a11y/prefer-tag-over-role": "off",
        "jsx-a11y/role-has-required-aria-props": "error",
        "jsx-a11y/role-supports-aria-props": "error",
        "jsx-a11y/scope": "error",
        "jsx-a11y/tabindex-no-positive": "error",
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
        "typescript/await-thenable": "error",
        "typescript/ban-ts-comment": "error",
        "typescript/consistent-return": "off",
        "typescript/consistent-type-definitions": "off",
        "typescript/no-array-constructor": "error",
        "typescript/no-array-delete": "error",
        "typescript/no-base-to-string": "error",
        "typescript/no-duplicate-enum-values": "error",
        "typescript/no-duplicate-type-constituents": "error",
        "typescript/no-empty-object-type": "error",
        "typescript/no-explicit-any": "error",
        "typescript/no-extra-non-null-assertion": "error",
        "typescript/no-floating-promises": "error",
        "typescript/no-for-in-array": "error",
        "typescript/no-implied-eval": "error",
        "typescript/no-misused-new": "error",
        "typescript/no-misused-promises": "error",
        "typescript/no-namespace": "error",
        "typescript/no-non-null-asserted-optional-chain": "error",
        "typescript/no-redundant-type-constituents": "error",
        "typescript/no-require-imports": "error",
        "typescript/no-this-alias": "error",
        "typescript/no-unnecessary-type-assertion": "error",
        "typescript/no-unnecessary-type-constraint": "error",
        "typescript/no-unsafe-argument": "error",
        "typescript/no-unsafe-assignment": "error",
        "typescript/no-unsafe-call": "error",
        "typescript/no-unsafe-declaration-merging": "error",
        "typescript/no-unsafe-enum-comparison": "error",
        "typescript/no-unsafe-function-type": "error",
        "typescript/no-unsafe-member-access": "error",
        "typescript/no-unsafe-return": "error",
        "typescript/no-unsafe-unary-minus": "error",
        "typescript/no-unused-expressions": "error",
        "typescript/no-unused-vars": "error",
        "typescript/no-wrapper-object-types": "error",
        "typescript/only-throw-error": "error",
        "typescript/prefer-as-const": "error",
        "typescript/prefer-namespace-keyword": "error",
        "typescript/prefer-promise-reject-errors": "error",
        "typescript/prefer-readonly-parameter-types": "off",
        "typescript/require-await": "error",
        "typescript/restrict-plus-operands": "error",
        "typescript/restrict-template-expressions": "error",
        "typescript/triple-slash-reference": "error",
        "typescript/unbound-method": "error",
        "typescript/switch-exhaustiveness-check": [
          "error",
          {
            considerDefaultExhaustiveForUnions: true,
          },
        ],
      },
      plugins: ["typescript"],
    },
    {
      files: ["src/**/*.ts", "src/**/*.tsx"],
      rules: {
        "no-restricted-globals": [
          "error",
          {
            globals: [
              {
                name: "fetch",
                message: "Use the validated browser API boundary in src/api/transport.ts.",
              },
              {
                name: "XMLHttpRequest",
                message: "Use the validated browser API boundary in src/api/transport.ts.",
              },
              {
                name: "WebSocket",
                message: "Use the validated browser API boundary in src/api/transport.ts.",
              },
              {
                name: "EventSource",
                message: "Use the validated browser API boundary in src/api/transport.ts.",
              },
            ],
            checkGlobalObject: true,
          },
        ],
      },
      excludeFiles: ["src/api/transport.ts"],
    },
    {
      files: [
        "src/api/**/*.ts",
        "src/api/**/*.tsx",
        "src/operation/**/*.ts",
        "src/operation/**/*.tsx",
        "src/hooks/**/*.ts",
        "src/hooks/**/*.tsx",
        "src/hooks/operation/operationDelivery.ts",
      ],
      rules: {
        "no-restricted-imports": [
          "error",
          {
            patterns: [
              {
                group: ["**/presentation", "**/presentation/**"],
                message: "Keep locale and rendered text out of operation and request coordination.",
              },
            ],
          },
        ],
        "no-restricted-globals": [
          "error",
          {
            globals: [
              {
                name: "Intl",
                message: "Operation state and transport must remain presentation-independent.",
              },
              {
                name: "localStorage",
                message: "Operation state and transport must remain presentation-independent.",
              },
              {
                name: "sessionStorage",
                message: "Operation state and transport must remain presentation-independent.",
              },
              {
                name: "navigator",
                message: "Operation state and transport must remain presentation-independent.",
              },
              {
                name: "fetch",
                message: "Operation state and transport must remain presentation-independent.",
              },
              {
                name: "XMLHttpRequest",
                message: "Operation state and transport must remain presentation-independent.",
              },
              {
                name: "WebSocket",
                message: "Operation state and transport must remain presentation-independent.",
              },
              {
                name: "EventSource",
                message: "Operation state and transport must remain presentation-independent.",
              },
            ],
            checkGlobalObject: true,
          },
        ],
      },
      excludeFiles: ["src/api/transport.ts"],
    },
    {
      files: ["src/api/transport.ts"],
      rules: {
        "no-restricted-imports": [
          "error",
          {
            patterns: [
              {
                group: ["**/presentation", "**/presentation/**"],
                message: "Keep locale and rendered text out of operation and request coordination.",
              },
            ],
          },
        ],
        "no-restricted-globals": [
          "error",
          {
            globals: [
              {
                name: "Intl",
                message: "Operation state and transport must remain presentation-independent.",
              },
              {
                name: "localStorage",
                message: "Operation state and transport must remain presentation-independent.",
              },
              {
                name: "sessionStorage",
                message: "Operation state and transport must remain presentation-independent.",
              },
              {
                name: "navigator",
                message: "Operation state and transport must remain presentation-independent.",
              },
            ],
            checkGlobalObject: true,
          },
        ],
      },
    },
    {
      files: ["tests/**", "e2e/**"],
      env: {
        vitest: true,
      },
    },
    {
      files: ["tests/**/*.ts", "tests/**/*.tsx", "e2e/**/*.ts", "e2e/**/*.tsx"],
      rules: {
        "typescript/strict-void-return": "off",
        "typescript/no-unsafe-type-assertion": "off",
      },
      plugins: ["typescript"],
    },
    {
      files: ["tests/**", "e2e/**", "scripts/**"],
      rules: {
        "no-await-in-loop": "off",
      },
    },
    {
      files: ["src/api/**", "src/hooks/**", "src/operation/**"],
      rules: {
        "no-restricted-imports": [
          "error",
          {
            patterns: [
              {
                group: ["**/api/transport", "**/api/transport.ts"],
                message: "Use the service facade (src/api/service.ts).",
              },
              {
                group: ["**/views/**", "**/components/**", "**/presentation/**"],
                message: "Lower layers must not import presentation layers.",
              },
            ],
          },
        ],
      },
    },
    {
      files: ["src/api/service.ts"],
      rules: {
        "no-restricted-imports": [
          "error",
          {
            patterns: [
              {
                group: ["**/views/**", "**/components/**", "**/presentation/**"],
                message: "Lower layers must not import presentation layers.",
              },
            ],
          },
        ],
      },
    },
    {
      files: ["src/protocol/**", "**/contracts/**"],
      env: { browser: false, node: false },
      rules: {
        "no-restricted-imports": [
          "error",
          {
            patterns: [
              {
                group: [
                  "react",
                  "react-*",
                  "node:*",
                  "**/views/**",
                  "**/components/**",
                  "**/presentation/**",
                  "**/hooks/**",
                  "**/operation/**",
                  "**/utils/**",
                ],
                message: "Protocol modules are pure and import only protocol/contracts.",
              },
              {
                group: ["**/api/**"],
                allowTypeImports: false,
                message: "Protocol modules cannot import API implementation or types.",
              },
            ],
          },
        ],
        "no-restricted-globals": [
          "error",
          {
            globals: [
              "fetch",
              "XMLHttpRequest",
              "WebSocket",
              "EventSource",
              "setTimeout",
              "setInterval",
              "require",
              "Intl",
              "crypto",
              "process",
              "window",
              "navigator",
              "localStorage",
              "sessionStorage",
              "performance",
              "Date",
              "globalThis",
              "global",
              "self",
            ],
            checkGlobalObject: true,
          },
        ],
        "no-restricted-properties": [
          "error",
          {
            object: "Date",
            property: "now",
          },
          {
            object: "Math",
            property: "random",
          },
          {
            object: "globalThis",
            property: "fetch",
          },
          {
            object: "globalThis",
            property: "crypto",
          },
        ],
      },
    },
    {
      files: ["src/protocol/**", "**/contracts/**"],
      rules: {
        "import-boundary/no-unresolved": "error",
        "import-boundary/no-nodejs-modules": "error",
        "import-boundary/no-restricted-paths": [
          "error",
          {
            zones: [
              {
                target: ["./src/protocol", "../contracts/!(messages).ts"],
                from: root,
                except: ["./src/protocol/", "../contracts/"].map(allowed),
                message: "Pure modules import only protocol and contracts.",
              },
              {
                target: "../contracts/messages.ts",
                from: root,
                except: [
                  "../src/ClaimCore.Protocol/Localization/en.json",
                  "../src/ClaimCore.Protocol/Localization/lv.json",
                  "../src/ClaimCore.Protocol/Localization/ar.json",
                  "./src/protocol/",
                  "../contracts/",
                ].map(allowed),
              },
            ],
          },
        ],
        "import/no-dynamic-require": ["error", { esmodule: true }],
      },
    },
    {
      files: ["src/api/**", "src/operation/**", "src/hooks/**"],
      rules: {
        "import-boundary/no-restricted-paths": [
          "error",
          {
            zones: [
              {
                target: ["./src/api", "./src/operation", "./src/hooks"],
                from: "./src/presentation",
                message: "Keep presentation outside transport and request coordination.",
              },
            ],
          },
        ],
        "import/no-dynamic-require": ["error", { esmodule: true }],
      },
    },
  ],
});

export default config;
