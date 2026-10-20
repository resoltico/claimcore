import { expect, it } from "vitest";
import { assembleAct, assembleBoundAct } from "../src/interaction/drafts";
it("a synthetic act and renamed input bind through metadata without named-command rules", () => {
  const binding = {
    kind: "SYNTHETIC_ACT",
    fields: [{ fieldName: "syntheticInput", path: ["assertion", "newMember"] }],
  };
  expect(assembleBoundAct(binding, { syntheticInput: "synthetic value" })).toEqual({
    kind: "SYNTHETIC_ACT",
    assertion: { newMember: "synthetic value" },
  });
  expect(() => assembleAct(binding, { syntheticInput: "synthetic value" })).toThrow("typed codec");
  expect(() =>
    assembleBoundAct(
      {
        kind: "SYNTHETIC_ACT",
        fields: [{ fieldName: "syntheticInput", path: ["__proto__", "polluted"] }],
      },
      {},
    ),
  ).toThrow("binding path");
});
