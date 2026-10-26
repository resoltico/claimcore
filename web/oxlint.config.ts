import baseJson from "./.oxlintrc.json" with { type: "json" };
import sharedJson from "../config/oxlint.json" with { type: "json" };
import { defineConfig, type OxlintConfig } from "oxlint";
import { fileURLToPath } from "node:url";
import { parse } from "node:path";

// JSON imports widen rule levels and tuples; oxlint validates these existing configs at load time.
// oxlint-disable-next-line typescript/no-unsafe-type-assertion -- oxlint validates imported JSON configuration when loading it.
const base = baseJson as unknown as OxlintConfig;
// oxlint-disable-next-line typescript/no-unsafe-type-assertion -- oxlint validates imported JSON configuration when loading it.
const shared = sharedJson as unknown as OxlintConfig;
const checkout = fileURLToPath(new URL("../", import.meta.url));
const { root } = parse(checkout);
const allowed = (path: string) => fileURLToPath(new URL(path, import.meta.url)).slice(root.length);

const config: OxlintConfig = defineConfig({
  ...base,
  extends: [shared],
  jsPlugins: [
    ...(base.jsPlugins ?? []),
    { name: "import-boundary", specifier: "eslint-plugin-import-x" },
    "./lint/maintained-rules.mjs",
  ],
  settings: {
    "import-x/resolver": { typescript: { project: "tsconfig.app.json" } },
  },
  overrides: [
    ...(base.overrides ?? []),
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
                except: ["./src/protocol/", "../contracts/", "./src/api/types.ts"].map(allowed),
                message: "Pure modules import only protocol/contracts and api types.",
              },
              {
                target: "../contracts/messages.ts",
                from: root,
                except: [
                  "../src/ClaimCore.Contract/Localization/en.json",
                  "../src/ClaimCore.Contract/Localization/lv.json",
                  "../src/ClaimCore.Contract/Localization/ar.json",
                  "./src/protocol/",
                  "../contracts/",
                ].map(allowed),
              },
            ],
          },
        ],
        "eslint-selectors/no-restricted-syntax": [
          "error",
          {
            selector: "ImportExpression[source.type!='Literal']",
            message:
              "Pure module imports must use a literal path so the import boundary can be checked.",
          },
        ],
      },
    },
  ],
});

export default config;
