import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

export const casesBreadcrumb = () =>
  within(screen.getByRole("navigation", { name: "Breadcrumb" })).getByRole("link", {
    name: "Cases",
  });

export const workspaceSignOut = async (user = userEvent.setup()) => {
  const visible =
    screen.queryByRole("menuitem", { name: "Sign out" }) ??
    screen.queryByRole("button", { name: "Sign out" });
  if (visible !== null) {
    return visible;
  }
  await user.click(screen.getByRole("button", { name: /^Account(?: · |$)/u }));
  return screen.getByRole("menuitem", { name: "Sign out" });
};

export const rowActions = (row: HTMLElement) =>
  within(row).getByRole("button", { name: /^Actions for /u });

export const selectRowAction = async (
  user: ReturnType<typeof userEvent.setup>,
  row: HTMLElement,
  label: string,
) => {
  await user.click(rowActions(row));
  await user.click(screen.getByRole("menuitem", { name: label }));
};
