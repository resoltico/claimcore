import { expect, it } from "vitest";
import { webCases, isObject } from "./contract-corpus.fixtures";
import { definition } from "./v3-ui.fixtures";
import requirements from "../src/presentation/generated/diagnostic-parameters.json";
import { renderKey } from "../src/presentation/messages";
import { defaults } from "../src/presentation/preferences";
import { createPresenter } from "../src/presentation/presenter";
const semantic = definition.definition;

const nativeText = (value: unknown) => {
  if (!isObject(value)) {
    throw new Error("Missing native codec fixture.");
  }
  const data = isObject(value["outcome"]) ? value["outcome"]["data"] : value;
  if (!isObject(data) || typeof data["message"] !== "string" || !isObject(data["diagnostic"])) {
    throw new Error("Missing native codec diagnostic.");
  }
  const { diagnostic } = data;
  if (typeof diagnostic["id"] !== "string" || !isObject(diagnostic["parameters"])) {
    throw new Error("Malformed native diagnostic fixture.");
  }
  const values = Object.fromEntries(
    Object.entries(diagnostic["parameters"]).map(([key, parameter]) => {
      if (typeof parameter !== "number") {
        throw new Error("Diagnostic fixture contains nonnumeric parameters.");
      }
      return [key, String(parameter)];
    }),
  );
  return { id: diagnostic["id"], message: data["message"], values };
};

it("matches every native English diagnostic literal under default presentation [CC-WEB-001]", () => {
  // These IDs identify production-codec originals; translated-copy variants are deliberately valid.
  const prefixes = ["valid-host-", "diagnostic-", "fault-", "recovery-diagnostic-"];
  const seen = new Set<string>();
  const samples = webCases();
  for (const id of Object.keys(requirements)) {
    const sample = samples.find(
      (item) => item.valid && prefixes.some((prefix) => item.id === `${prefix}${id}`),
    );
    if (sample === undefined) {
      throw new Error(`Native diagnostic fixture omitted ${id}.`);
    }
    const actual = nativeText(sample.value);
    expect(actual.id).toBe(id);
    expect(renderKey(defaults, `diagnostic.${id}`, actual.values)).toBe(actual.message);
    seen.add(id);
  }
  expect(seen).toEqual(new Set(Object.keys(requirements)));
});

it("preserves independent English diagnostic knowledge and parameter examples", () => {
  expect(renderKey(defaults, "diagnostic.INPUT_TEXT_TOO_LONG", { maximumCharacters: "200" })).toBe(
    "The value must not exceed 200 Unicode characters.",
  );
  expect(
    renderKey(defaults, "diagnostic.INPUT_DECIMAL_FORMAT", {
      maximumIntegerDigits: "18",
      maximumFractionalDigits: "4",
    }),
  ).toBe(
    "Use non-negative decimal text: up to 18 integer digits and 4 fractional digits; no sign or exponent.",
  );
  expect(renderKey(defaults, "diagnostic.WEB_HOST_COMPLETED_RESPONSE_FAILED")).toBe(
    "Request processing completed but its response could not be delivered.",
  );
  expect(renderKey(defaults, "diagnostic.CORE_COMMIT_OUTCOME_UNKNOWN")).toBe(
    "Commit completion was not confirmed.",
  );
});

it("derives shared browser business meaning directly from semantic discovery", () => {
  const p = createPresenter(defaults);
  for (const field of semantic.fields) {
    expect(p.fieldLabel(field.name)).toBe(field.label);
    expect(p.fieldMeaning(field.name)).toBe(field.meaning);
  }
  for (const command of semantic.commands) {
    expect(p.commandLabel(command.kind)).toBe(command.label);
    expect(p.commandMeaning(command.kind)).toBe(command.meaning);
    if (command.inputs.kind === "CORRECTION_GROUPS") {
      for (const group of command.inputs.groups) {
        expect(p.groupLabel(group.name)).toBe(group.label);
        expect(p.groupMeaning(group.name)).toBe(group.meaning);
      }
    }
  }
});
