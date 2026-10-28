import { browserTransportPolicy } from "./browser-transport-lint.config.ts";
import { typedPresentationPolicy } from "./typed-presentation-lint.config.ts";
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
    "func-style": "off",
    "id-length": "off",
    "init-declarations": "off",
    "max-lines": ["error", { max: 500, skipBlankLines: false, skipComments: false }],
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
    "no-continue": "off",
    "no-empty": "error",
    "no-empty-function": "error",
    "no-eq-null": "error",
    "no-inline-comments": "off",
    "no-magic-numbers": "off",
    "no-regex-spaces": "error",
    "no-ternary": "off",
    "no-use-before-define": [
      "error",
      {
        functions: false,
        classes: true,
        variables: true,
      },
    ],
    "no-useless-assignment": "error",
    "no-var": "error",
    "one-var": ["error", "never"],
    "prefer-named-capture-group": "off",
    "sort-imports": "off",
    "sort-keys": "off",
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
    { files: ["**/contracts/**", "src/generated/**"], rules: { "max-lines": "off" } },
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
    typedPresentationPolicy,
    browserTransportPolicy,
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
                  "../src/ClaimCore.Protocol/Messages/Localization/en.json",
                  "../src/ClaimCore.Protocol/Messages/Localization/lv.json",
                  "../src/ClaimCore.Protocol/Messages/Localization/ar.json",
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
