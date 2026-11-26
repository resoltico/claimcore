import userEvent from "@testing-library/user-event";
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
import { expect, it, vi } from "vitest";
import { render, screen, testWorkspaceIdentity } from "../../support/presentation-test-support";
import { actorRefInput, workBasis } from "../../support/inputs";

import { Shell as Header } from "../../../src/shell/Shell";

const Shell = (props: Omit<ComponentProps<typeof Header>, "presentation">) => {
  const requester = useRequesterContext();
  return (
    <RecoveryProvider identity={requester === null ? null : testWorkspaceIdentity}>
      <Header {...props} presentation={requester?.screen ?? null} />
    </RecoveryProvider>
  );
};

it("shows only supplied account identity and keeps navigation independent from raw assignments", () => {
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
                key: "self",
                labelId: "label.account",
                prominence: "PRIMARY",
                value: {
                  kind: "ACTOR",
                  value: actorRefInput({
                    displayName: { kind: "NAMED", value: "Supplied account name" },
                  }),
                },
              },
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
    <Shell
      route={{ screen: "cases", query: "" }}
      active="cases"
      views={[]}
      tasks={[]}
      locked={false}
    />
  );
  const view = render(<RequesterContext value={requester}>{header}</RequesterContext>);
  expect(screen.getByRole("button", { name: /^Account(?: · |$)/u })).toHaveTextContent(
    "Supplied account name",
  );
  expect(screen.queryByLabelText("Roles")).toBeNull();
  expect(screen.queryByRole("link", { name: "People and access" })).toBeNull();
  expect(screen.queryByRole("link", { name: "People history" })).toBeNull();
  expect(document.body).not.toHaveTextContent("https://issuer.example/");
  expect(document.body).not.toHaveTextContent(requester.subject.actorId);
  view.rerender(<RequesterContext value={null}>{header}</RequesterContext>);
  expect(screen.queryByLabelText("Roles")).toBeNull();
  expect(document.body).not.toHaveTextContent("Supplied account name");
});

it("offers named global Find from other screens and respects the lock", async () => {
  const launchFind = vi.fn();
  const header = (locked: boolean) => (
    <Shell
      route={{ screen: "reminders", query: "" }}
      active="reminders"
      views={["case.list"]}
      tasks={[]}
      locked={locked}
      onFind={launchFind}
    />
  );
  const view = render(header(false));
  const trigger = screen.getByRole("button", { name: /^Find a case/u });
  expect(trigger).toBeEnabled();
  await userEvent.click(trigger);
  expect(launchFind).toHaveBeenCalledWith(trigger.id);
  const modifier = /Mac|iPhone|iPad|iPod/u.test(navigator.platform) ? "Meta" : "Control";
  const shortcut = `{${modifier}>}k{/${modifier}}`;
  await userEvent.keyboard(shortcut);
  expect(launchFind).toHaveBeenLastCalledWith();
  view.rerender(header(true));
  expect(trigger).toBeDisabled();
  await userEvent.keyboard(shortcut);
  expect(launchFind).toHaveBeenCalledTimes(2);
});

it("keeps compact sidebar links named when their visual tooltip captions are hidden", () => {
  render(
    <Shell
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
