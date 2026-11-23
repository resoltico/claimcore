import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

export const casesBreadcrumb = () =>
  within(screen.getByRole("navigation", { name: "Breadcrumb" })).getByRole("link", {
    name: "Cases",
  });

export const workspaceSignOut = async (user = userEvent.setup()) => {
  const visible = screen.queryByRole("menuitem", { name: "Sign out" });
  if (visible !== null) {
    return visible;
  }
  await user.click(screen.getByRole("button", { name: "Account" }));
  return screen.getByRole("menuitem", { name: "Sign out" });
};
