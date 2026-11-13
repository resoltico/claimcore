import { expect, it } from "vitest";
import { peoplePrimaryAction } from "../../../src/operation/peopleActionPriority";
it.each([
  {
    offered: ["people.disable", "people.revoke", "people.grant", "people.enable"],
    expected: "people.grant",
  },
  {
    offered: ["people.enable", "people.grant", "people.disable", "people.revoke"],
    expected: "people.grant",
  },
  { offered: ["people.grant", "people.revoke"], expected: "people.grant" },
  { offered: ["people.disable", "people.revoke"], expected: "people.revoke" },
  { offered: ["people.enable"], expected: "people.enable" },
  { offered: ["OTHER"], expected: undefined },
  { offered: [], expected: undefined },
])(
  "ranks offered administration tasks independently of declaration order: $offered",
  ({ offered, expected }) => {
    expect(peoplePrimaryAction(offered)).toBe(expected);
  },
);
