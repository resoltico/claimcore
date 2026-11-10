import type { ActiveRequester } from "../../../src/hooks/useRequesterContext";
import { activeRequester } from "../../support/human-requester.fixtures";
import { RecoveryProvider } from "../../../src/recovery/RecoveryContext";
import type { ComponentProps } from "react";
import { expect, it, vi } from "vitest";
import { within } from "@testing-library/react";
import { render, screen } from "../../support/presentation-test-support";
import { workBasis } from "../../support/inputs";
import { RequesterContext, useRequesterContext } from "../../../src/hooks/useRequesterContext";
import { WorkspaceHeader as Header } from "../../../src/views/WorkspaceHeader";

const WorkspaceHeader = (props: Omit<ComponentProps<typeof Header>, "presentation">) => {
  const requester = useRequesterContext();
  return (
    <RecoveryProvider>
      <Header {...props} presentation={requester?.screen ?? null} />
    </RecoveryProvider>
  );
};

it("shows only supplied own role facts and keeps navigation independent from raw assignments", () => {
  const requester: ActiveRequester = {
    ...activeRequester,
    basis: { kind: "SNAPSHOT" as const, observation: workBasis },
    screen: {
      regions: [
        {
          role: "IDENTITY" as const,
          content: {
            kind: "ENTRIES" as const,
            entries: [
              {
                key: "roles",
                labelId: "field.role.label",
                prominence: "PRIMARY" as const,
                value: { kind: "CODES" as const, vocabulary: "role", tokens: ["OWNER"] },
              },
            ],
          },
        },
      ],
    },
    context: { ...activeRequester.context, tasks: [], views: [] },
  };
  const header = (
    <WorkspaceHeader
      route={{ screen: "cases", query: "" }}
      active="cases"
      views={[]}
      tasks={[]}
      locked={false}
      openCase={vi.fn()}
    />
  );
  const view = render(<RequesterContext value={requester}>{header}</RequesterContext>);
  expect(screen.getByLabelText("Roles")).toHaveTextContent("Owner");
  expect(screen.queryByRole("link", { name: "People & access" })).toBeNull();
  expect(screen.queryByRole("link", { name: "Authority history" })).toBeNull();
  expect(document.body).not.toHaveTextContent("https://issuer.example/");
  expect(document.body).not.toHaveTextContent(requester.context.actorId);
  view.rerender(<RequesterContext value={null}>{header}</RequesterContext>);
  expect(screen.queryByLabelText("Roles")).toBeNull();
  expect(document.body).not.toHaveTextContent("Owner");
});

it("keeps the shell search an explicitly secondary, labelled submit and preserves its lock", () => {
  const header = (locked: boolean) => (
    <WorkspaceHeader
      route={{ screen: "cases", query: "" }}
      active="cases"
      views={["CASES"]}
      tasks={[]}
      locked={locked}
      openCase={vi.fn()}
    />
  );
  const view = render(header(false));
  const search = screen.getByRole("search", { name: "Search cases" });
  const submit = within(search).getByRole("button", { name: "Search" });
  expect(submit).toHaveAttribute("type", "submit");
  expect(submit.classList.contains("ui-button--secondary")).toBe(true);
  expect(submit).not.toBeDisabled();
  view.rerender(header(true));
  expect(submit).toBeDisabled();
  expect(within(search).getByRole("searchbox", { name: "Search cases" })).toBeDisabled();
});

it("keeps compact sidebar links named when their visual tooltip captions are hidden", () => {
  render(
    <WorkspaceHeader
      route={{ screen: "cases", query: "" }}
      active="cases"
      views={["CASES"]}
      tasks={[]}
      locked={false}
      openCase={vi.fn()}
    />,
  );
  const link = document.querySelector<HTMLAnchorElement>(
    '.workspace-sidebar nav a[href="/cases"]',
  )!;
  const caption = link.querySelector<HTMLElement>(".shell-link-label")!;
  expect(link).toHaveAccessibleName("Cases 0");
  caption.style.visibility = "hidden";
  expect(link).toHaveAccessibleName("Cases 0");
});
