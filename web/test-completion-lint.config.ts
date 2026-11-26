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
      ...["todo", "skipIf", "runIf", "fails", "fixme"].map((property) => ({
        property,
        message: "Required tests must execute and pass in their configured scope.",
      })),
      ...["skip", "fail"].map((property) => ({
        object: "test",
        property,
        message:
          "Select the supported test scope in runner configuration. Assert expected product errors in ordinary tests.",
      })),
    ],
  },
});
