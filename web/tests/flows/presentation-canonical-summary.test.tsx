import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { RecordView } from "../../src/ui/RecordView";
import { createPresenter } from "../../src/presentation/presenter";
import { defaults, resolveLanguage, preferenceKey } from "../../src/presentation/preferences";
import { render, screen } from "../support/presentation-test-support";

it.each(["en", "lv", "ar"])(
  "copies supplied canonical values and absent markers in %s",
  async (language) => {
    const user = userEvent.setup();
    const copy = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText: copy },
    });
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({ ...defaults, version: 3, language, displayLocale: "ar-EG" }),
    );
    render(
      <RecordView
        view={{
          kind: "RECORD",
          sections: [
            {
              titleId: "subject.CASE.label",
              entries: [
                {
                  key: "exactMoney",
                  labelId: "field.claimedAmount.label",
                  prominence: "PRIMARY",
                  value: {
                    kind: "MONEY",
                    amount: "999999999999999999.0100",
                    currency: "EUR",
                    displayScale: 4,
                  },
                },
                {
                  key: "authored",
                  labelId: "field.claimantName.label",
                  prominence: "PRIMARY",
                  value: { kind: "PARTY", value: "A\u0308 العربية\u200D" },
                },
                {
                  key: "missing",
                  labelId: "field.paymentDate.label",
                  prominence: "DETAIL",
                  value: { kind: "ABSENT", reasonId: "label.notRecorded" },
                },
              ],
            },
          ],
        }}
      />,
    );
    const p = createPresenter({
      ...defaults,
      language: resolveLanguage(language),
      displayLocale: "ar-EG",
    });
    await user.click(
      screen.getByRole("button", {
        name: p.text("ui.copy", { label: p.text("label.recordDetails") }),
      }),
    );
    expect(copy).toHaveBeenCalledWith(
      "exactMoney: 999999999999999999.0100 EUR\nauthored: A\u0308 العربية\u200D\nmissing: null",
    );
    expect(document.querySelector('bdi[dir="ltr"]')).not.toBeNull();
  },
);
