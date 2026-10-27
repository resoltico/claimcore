import type { OxlintConfig } from "oxlint";

export const browserTransportPolicy: NonNullable<OxlintConfig["overrides"]>[number] = {
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
};
