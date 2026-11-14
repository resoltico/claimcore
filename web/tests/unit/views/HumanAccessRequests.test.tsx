import type { Affordance, HumanReadDocument_people_requests } from "@contracts/client";
import { operationAffordance } from "../../support/affordances.fixtures";
import { requestText } from "../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import { AccessRequests } from "../../../src/views/people/AccessRequests";

const principals = ["first", "second"].map((subject) => ({
  kind: "HUMAN" as const,
  issuer: "https://issuer.example/",
  subject,
}));
const handles = principals.map((principal) =>
  operationAffordance(
    "people.admit",
    { kind: "PEOPLE" },
    {
      "input:principal.kind": principal.kind,
      "input:principal.issuer": principal.issuer,
      "input:principal.subject": principal.subject,
      "input:profile.displayName": `${principal.subject} unverified hint`,
      "input:profile.contact": null,
    },
  ),
);
const dismiss = (index: number): Affordance => ({
  task: "people.requests.dismiss",
  binding: { kind: "PEOPLE" },
  seeds: {
    "input:principal.kind": principals[index]!.kind,
    "input:principal.issuer": principals[index]!.issuer,
    "input:principal.subject": principals[index]!.subject,
  },
  editable: [],
  choices: {},
  primary: false,
  blocked: null,
});
const document: HumanReadDocument_people_requests = {
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            { key: "caption", labelId: "label.details", prominence: "PRIMARY", isMoney: false },
          ],
          rows: [
            {
              key: "second",
              actions: [handles[1]!, dismiss(1)],
              values: [{ kind: "TEXT", value: "Core second caption Ω" }],
            },
            {
              key: "first",
              actions: [handles[0]!, dismiss(0)],
              values: [{ kind: "TEXT", value: "Core first caption Ψ" }],
            },
          ],
        },
      },
    ],
  },
  subject: { kind: "LIST" },
  actions: [],
  links: [],
  history: null,
  nextCursor: null,
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-09T12:00:00.000000Z",
      businessDate: "2026-11-09",
      timeZone: "Etc/UTC",
    },
  },
  disclosure: "CURRENT",
};
const reply = (value: unknown) =>
  new Response(
    JSON.stringify({ representation: "HUMAN", outcome: { kind: "SUCCEEDED", document: value } }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
afterEach(() => vi.unstubAllGlobals());

it("binds Core request row keys to unverified admission controls without reconstructing captions from claims", async () => {
  vi.stubGlobal("fetch", vi.fn<typeof globalThis.fetch>().mockResolvedValue(reply(document)));
  const admit = vi.fn();
  render(<AccessRequests available busy={false} onAdmit={admit} onDisclosureRefused={vi.fn()} />);
  await screen.findByText("Core second caption Ω");
  await userEvent.click(screen.getAllByRole("button", { name: "Add person" })[0]!);
  expect(admit).toHaveBeenCalledWith(handles[1]);
  expect(screen.queryByText("second unverified hint")).not.toBeInTheDocument();
});

it("dismisses only the selected principal and refreshes once after a confirmed HUMAN result", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(reply(document))
    .mockResolvedValueOnce(
      reply({
        screen: { regions: [] },
        subject: { kind: "LIST" },
        actions: [],
        links: [],
        history: null,
        nextCursor: null,
        basis: document.basis,
        disclosure: "CURRENT",
      }),
    )
    .mockResolvedValueOnce(
      reply({
        ...document,
        screen: { regions: [] },
      }),
    );
  vi.stubGlobal("fetch", fetch);
  render(<AccessRequests available busy={false} onAdmit={vi.fn()} onDisclosureRefused={vi.fn()} />);
  await screen.findByText("Core second caption Ω");
  await userEvent.click(screen.getAllByRole("button", { name: /dismiss/iu })[0]!);
  const dialog = screen.getByRole("dialog");
  expect(dialog).toHaveTextContent("Core second caption Ω");
  expect(dialog).not.toHaveTextContent("Core first caption Ψ");
  const { within } = await import("@testing-library/react");
  await userEvent.click(within(dialog).getByRole("button", { name: /dismiss/iu }));
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(3);
  });
  expect(fetch.mock.calls[1]![0]).toBe("/api/people.requests.dismiss");
  expect(JSON.parse(requestText(fetch.mock.calls[1]![1]?.body))).toEqual(principals[1]);
});
