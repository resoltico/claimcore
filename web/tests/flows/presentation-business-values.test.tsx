import { expect, it } from "vitest";
import { CharacterWarning } from "../../src/components/CharacterWarning";
import { PresentedValue } from "../../src/components/PresentedValue";
import { render, screen } from "../support/presentation-test-support";

it("isolates and warns about permitted business Unicode without normalizing or executing markup", () => {
  const value = "A\u0308 <script> العربية\u200D\u202E";
  render(
    <>
      <PresentedValue value={{ kind: "TEXT", value }} timeZone="Etc/UTC" />
      <CharacterWarning value={null} />
    </>,
  );
  expect(screen.getByText(value).tagName).toBe("BDI");
  expect(screen.getByText("Contains U+200D, U+202E")).toBeVisible();
  expect(document.querySelector("script")).toBeNull();
});
