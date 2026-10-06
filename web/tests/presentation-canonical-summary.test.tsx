import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { CaseFieldsView } from "../src/components/CaseFieldsView";
import { preferenceKey } from "../src/presentation/preferences";
import { definition, fields } from "./v3-ui.fixtures";
import { render, screen } from "./presentation-test-support";

it.each(["en", "lv", "ar"])(
  "copies canonical summary business values and null markers in %s",
  async (language) => {
    const user = userEvent.setup();
    const copy = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText: copy },
    });
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({ version: 1, language, displayLocale: "ar-EG" }),
    );
    render(
      <CaseFieldsView
        caseView={{
          fields: {
            ...fields,
            claimedAmount: "999999999999999999.0100",
            claimantName: "A\u0308 العربية\u200D",
          },
          revision: "9223372036854775806",
        }}
        fields={definition.definition.fields}
        context="synthetic"
      />,
    );
    await user.click(screen.getAllByRole("button")[0]!);
    const value: unknown = copy.mock.calls[0]?.[0];
    expect(typeof value).toBe("string");
    expect(value).toContain("9223372036854775806");
    expect(value).toContain("999999999999999999.0100");
    expect(value).toContain("2026-09-01");
    expect(value).toContain("A\u0308 العربية\u200D");
    expect(value).toContain("EUR");
    expect(value).toContain("OPENED");
    expect(
      typeof value === "string" && value.split("\n").filter((line) => line.endsWith(": null")),
    ).toHaveLength(4);
  },
);
