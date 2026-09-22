const stateFiles = [
  "src/api/**/*.{ts,tsx}",
  "src/domain/**/*.{ts,tsx}",
  "src/hooks/**/*.{ts,tsx}",
  "src/views/recovery/RecoveryState.ts",
];
const localeGlobals = ["Intl", "localStorage", "sessionStorage", "navigator"];
const networkGlobals = ["fetch", "XMLHttpRequest", "WebSocket", "EventSource"];
const globalsRule = (names) => [
  "error",
  {
    globals: names.map((name) => ({
      name,
      message: "Operation state and transport must remain presentation-independent.",
    })),
    checkGlobalObject: true,
  },
];
const importsRule = [
  "error",
  {
    patterns: [
      {
        group: ["**/presentation", "**/presentation/**"],
        message: "Keep locale and rendered text out of operation and request coordination.",
      },
    ],
  },
];
// This qualifies declared dependencies and ambient locale access, not hostile computed/reflected code.
export const localeStateBoundaries = [
  {
    files: stateFiles,
    ignores: ["src/api/v2.ts"],
    rules: {
      "no-restricted-imports": importsRule,
      "no-restricted-globals": globalsRule([...localeGlobals, ...networkGlobals]),
    },
  },
  {
    files: ["src/api/v2.ts"],
    rules: {
      "no-restricted-imports": importsRule,
      "no-restricted-globals": globalsRule(localeGlobals),
    },
  },
];
export const dynamicLocaleImport = {
  selector: "ImportExpression[source.value=/presentation/]",
  message: "Do not load presentation from operation state or request coordination.",
};
