import { expect, it } from "vitest";
import { authorityPrimaryAction } from "../../../src/operation/authorityActionPriority";
it.each([
  { offered: ["DISABLE", "REVOKE", "GRANT", "ENABLE"], expected: "GRANT" },
  { offered: ["ENABLE", "GRANT", "DISABLE", "REVOKE"], expected: "GRANT" },
  { offered: ["GRANT", "REVOKE"], expected: "GRANT" },
  { offered: ["DISABLE", "REVOKE"], expected: "REVOKE" },
  { offered: ["ENABLE"], expected: "ENABLE" },
  { offered: ["OTHER"], expected: undefined },
  { offered: [], expected: undefined },
])(
  "ranks offered administration tasks independently of declaration order: $offered",
  ({ offered, expected }) => {
    expect(authorityPrimaryAction(offered)).toBe(expected);
  },
);
