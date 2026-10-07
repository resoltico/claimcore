// Every source path is linted under the TypeScript project that resolves its types. Type-aware
// rules read one program at a time, so a file checked under the wrong project sees `error` types.
export const lintProjects = [
  { tsconfig: "tsconfig.app.json", paths: ["src"] },
  { tsconfig: "tsconfig.test.json", paths: ["tests", "e2e"] },
  {
    tsconfig: "tsconfig.node.json",
    paths: ["scripts", "lint", "stylelint.config.mjs", "vite.config.ts", "playwright.config.ts"],
  },
];
