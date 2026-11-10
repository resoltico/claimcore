import { defineConfig } from "oxlint";

// A syntax-only pass keeps authored exclusions distinct from deliberate runner deselection.
export default defineConfig({
  plugins: ["vitest"],
  categories: {
    correctness: "off",
    suspicious: "off",
    pedantic: "off",
    perf: "off",
    style: "off",
    restriction: "off",
    nursery: "off",
  },
  rules: {
    "vitest/no-disabled-tests": "error",
    "vitest/no-focused-tests": "error",
    "eslint/no-restricted-properties": [
      "error",
      {
        property: "todo",
        message: "Required tests must execute; runner name filters are local selection.",
      },
      { property: "skipIf", message: "Required tests must execute on the configured platform." },
      { property: "runIf", message: "Required tests must execute on the configured platform." },
    ],
  },
});
