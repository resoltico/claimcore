import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { AboutPanel } from "../../../src/features/settings/AboutPanel";

it("offers this build's legal and source files without an authenticated session", () => {
  render(<AboutPanel />);
  expect(screen.getByText(/AGPL-3.0-only/u)).toBeVisible();
  expect(screen.getByRole("link", { name: "Source for this build" })).toHaveAttribute(
    "href",
    "/SOURCE.tar.gz",
  );
  expect(screen.getByRole("link", { name: "License" })).toHaveAttribute("href", "/LICENSE.txt");
  expect(screen.getByRole("link", { name: "Third-party notices" })).toHaveAttribute(
    "href",
    "/THIRD-PARTY-NOTICES.txt",
  );
  expect(screen.getByText(/No warranty/u)).toHaveTextContent(
    "You may redistribute under the license terms.",
  );
});
