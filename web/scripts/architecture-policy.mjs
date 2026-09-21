// All browser networking belongs to the validated API adapter. This is a source architecture
// rule, not a security sandbox against dynamic property lookup, reflection, or hostile scripts.
export const browserNetworkBoundary = {
  files: ["src/**/*.{ts,tsx}"],
  ignores: ["src/api/v2.ts", "src/generated/**"],
  rules: {
    "no-restricted-globals": [
      "error",
      {
        globals: ["fetch", "XMLHttpRequest", "WebSocket", "EventSource"].map((name) => ({
          name,
          message: "Use the validated browser API boundary in src/api/v2.ts.",
        })),
        checkGlobalObject: true,
      },
    ],
  },
};
