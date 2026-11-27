import { localNotice } from "../../src/protocol/notices";
import { render, screen } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { CopyValue } from "../../src/components/CopyValue";
import { CatalogField } from "../../src/components/CatalogField";
import { suspiciousCodePoints } from "../../src/utils/text";
import { definition } from "../support/service-ui.fixtures";

it("copies safe values and provides an accessible fallback when clipboard access is unavailable", async () => {
  const user = userEvent.setup();
  const writeText = vi.fn().mockResolvedValue(undefined);
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
  const view = render(<CopyValue label="reference" value="CC-55KW-SSJ4-8HMS-G1BH-73" />);
  await user.click(screen.getByRole("button", { name: "Copy reference" }));
  expect(writeText).toHaveBeenCalledWith("CC-55KW-SSJ4-8HMS-G1BH-73");
  expect(screen.getByText("reference copied.")).toBeVisible();
  writeText.mockRejectedValueOnce(new Error("denied"));
  await user.click(screen.getByRole("button", { name: "Copy reference" }));
  expect(screen.getByLabelText("reference copy fallback")).toHaveValue("CC-55KW-SSJ4-8HMS-G1BH-73");
  view.unmount();
});

it("associates catalog field errors", () => {
  render(
    <CatalogField
      field={definition.catalog.fields[0]!}
      value="CC-55KW-SSJ4-8HMS-G1BH-73"
      error={localNotice("unreachable")}
      onChange={vi.fn()}
    />,
  );
  expect(screen.getByText(/could not be reached/u)).toBeVisible();
});

it("presents suspicious Unicode defensively", () => {
  expect(suspiciousCodePoints("plain").length > 0).toBe(false);
  expect(suspiciousCodePoints("a\u202Eb").length > 0).toBe(true);
  expect(suspiciousCodePoints("a\u202Eb")).toEqual(["U+202E"]);
  expect(suspiciousCodePoints("\u0085\u00AD\u2028\u2029")).toEqual([
    "U+85",
    "U+AD",
    "U+2028",
    "U+2029",
  ]);
  expect(suspiciousCodePoints("العربية\u200D")).toEqual(["U+200D"]);
});
