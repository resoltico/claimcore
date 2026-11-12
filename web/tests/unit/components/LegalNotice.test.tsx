import { expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { LegalNotice } from "../../../src/components/LegalNotice";
it("offers this build's source and public terms without an authenticated session", () => {
  render(<LegalNotice />);
  expect(screen.getByText("ClaimCore · AGPL-3.0-only · License and source")).toBeVisible();
  expect(screen.getByRole("link", { name: "Source for this build", hidden: true })).toHaveAttribute(
    "href",
    "/SOURCE.tar.gz",
  );
  expect(screen.getByRole("link", { name: "License", hidden: true })).toHaveAttribute(
    "href",
    "/LICENSE.txt",
  );
  expect(screen.getByText(/No warranty/u)).toHaveTextContent(
    "You may redistribute under the license terms.",
  );
});
