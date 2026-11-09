import { presentationAccents } from "@contracts/client";
import { describe, expect, it } from "vitest";
import { contrastRatio, themeContrast } from "../../../src/theme/contrast";
import tokens from "../../../src/theme/tokens.json";

describe("semantic theme contrast gate", () => {
  it("implements every core accent", () => {
    expect(tokens.$extensions.claimcore.accents).toEqual(presentationAccents);
  });
  for (const accent of tokens.$extensions.claimcore.accents) {
    for (const mode of ["light", "dark"] as const) {
      it(`${accent}-${mode}: every declared pair meets its WCAG threshold`, () => {
        const measured = themeContrast(accent, mode);
        expect(measured.length).toBe(42);
        for (const pair of measured) {
          expect(
            pair.ratio,
            `${pair.meaning}: ${pair.foreground} on ${pair.background}`,
          ).toBeGreaterThanOrEqual(pair.minimum);
        }
      });
    }
  }
  it("rejects the former control boundaries in both modes", () => {
    // Independent RGB witnesses from the baseline tokens; no production color aliases.
    const hex = (channels: ReadonlyArray<number>) =>
      `#${channels.map((value) => value.toString(16).padStart(2, "0")).join("")}`;
    const former = [
      { boundary: [213, 224, 229], surface: [255, 255, 255] },
      { boundary: [54, 80, 93], surface: [24, 43, 55] },
    ];
    for (const palette of former) {
      expect(contrastRatio(hex(palette.boundary), hex(palette.surface))).toBeLessThan(3);
    }
  });
  it("does not silently accept malformed colors", () => {
    expect(() => contrastRatio("invalid", "invalid")).toThrow();
  });
});
