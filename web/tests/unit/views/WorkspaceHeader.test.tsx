import {
  installationActions,
  requesterLinks,
  activeRequester,
} from "../../support/human-requester.fixtures";
import {
  type ActiveRequester,
  RequesterContext,
  useRequesterContext,
} from "../../../src/hooks/useRequesterContext";

import { RecoveryProvider } from "../../../src/recovery/RecoveryContext";
import type { ComponentProps } from "react";
import { expect, it } from "vitest";
import { render, screen, testWorkspaceIdentity } from "../../support/presentation-test-support";
import { workBasis } from "../../support/inputs";

import { WorkspaceHeader as Header } from "../../../src/views/WorkspaceHeader";

const WorkspaceHeader = (props: Omit<ComponentProps<typeof Header>, "presentation">) => {
  const requester = useRequesterContext();
  return (
    <RecoveryProvider identity={requester === null ? null : testWorkspaceIdentity}>
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
    actions: installationActions([]),
    links: requesterLinks([], []),
  };
  const header = (
    <WorkspaceHeader
      route={{ screen: "cases", query: "" }}
      active="cases"
      views={[]}
      tasks={[]}
      locked={false}
    />
  );
  const view = render(<RequesterContext value={requester}>{header}</RequesterContext>);
  expect(screen.getByLabelText("Roles")).toHaveTextContent("Owner");
  expect(screen.queryByRole("link", { name: "People and access" })).toBeNull();
  expect(screen.queryByRole("link", { name: "People history" })).toBeNull();
  expect(document.body).not.toHaveTextContent("https://issuer.example/");
  expect(document.body).not.toHaveTextContent(requester.subject.actorId);
  view.rerender(<RequesterContext value={null}>{header}</RequesterContext>);
  expect(screen.queryByLabelText("Roles")).toBeNull();
  expect(document.body).not.toHaveTextContent("Owner");
});

it("offers named global search from other screens with a clean All selection", () => {
  const header = (locked: boolean) => (
    <WorkspaceHeader
      route={{ screen: "reminders", query: "" }}
      active="reminders"
      views={["case.list"]}
      tasks={[]}
      locked={locked}
    />
  );
  const view = render(header(false));
  const link = screen.getByRole("link", { name: "Search cases" });
  expect(link).toHaveAttribute("href", "/cases?view=ALL");
  expect(link).not.toHaveAttribute("aria-disabled");
  view.rerender(header(true));
  expect(link).toHaveAttribute("aria-disabled", "true");
});

it("keeps compact sidebar links named when their visual tooltip captions are hidden", () => {
  render(
    <WorkspaceHeader
      route={{ screen: "cases", query: "" }}
      active="cases"
      views={["case.list"]}
      tasks={[]}
      locked={false}
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
