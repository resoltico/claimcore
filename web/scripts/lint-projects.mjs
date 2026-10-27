// Every source path is linted under the TypeScript project that resolves its types. Type-aware
// rules read one program at a time, so a file checked under the wrong project sees `error` types.
export const lintProjects = [
  { tsconfig: "tsconfig.app.json", paths: ["src", "../contracts"] },
  { tsconfig: "tsconfig.test.json", paths: ["tests", "e2e"] },
  {
    tsconfig: "tsconfig.node.json",
    paths: [
      "scripts",
      "prettier.config.mjs",
      "oxlint.config.ts",
      "typed-presentation-lint.config.ts",
      "browser-transport-lint.config.ts",
      "vite.config.ts",
      "playwright.config.ts",
    ],
  },
];
