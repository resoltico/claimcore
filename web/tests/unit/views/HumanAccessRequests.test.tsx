import type { Affordance, HumanDocument } from "@contracts/client";
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
    { kind: "PEOPLE", expectedRevision: 0 },
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
  labelId: "endpoint.people.requests.dismiss.label",
  binding: {
    kind: "COMMAND",
    request: {
      command: "people.requests.dismiss",
      "input:principal.kind": principals[index]!.kind,
      "input:principal.issuer": principals[index]!.issuer,
      "input:principal.subject": principals[index]!.subject,
    },
  },
  seeds: {},
  editable: [],
  choices: {},

  blocked: null,
});
const document: HumanDocument = {
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "caption",
              labelId: "label.details",
              prominence: "PRIMARY",
              joins: null,
              isMoney: false,
            },
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
  await userEvent.click(screen.getAllByRole("button", { name: "Add from access request" })[0]!);
  expect(admit).toHaveBeenCalledWith(handles[1]);
  expect(screen.queryByText("second unverified hint")).not.toBeInTheDocument();
});

it("dismisses only the selected principal and refreshes once after a confirmed HUMAN result", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(reply(document))
    .mockResolvedValueOnce(
      reply({
        screen: {
          regions: [
            {
              role: "NOTICE",
              content: {
                kind: "NOTICE",
                notices: [{ messageId: "label.recordedChange", tone: "INFO" }],
              },
            },
          ],
        },
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
  expect(screen.getByText("Recorded change")).toBeVisible();
  expect(screen.queryByText("Access request dismissed.")).toBeNull();
  expect(fetch.mock.calls[1]![0]).toBe("/api/people.requests.dismiss");
  expect(JSON.parse(requestText(fetch.mock.calls[1]![1]?.body))).toEqual({
    "input:principal.kind": principals[1]!.kind,
    "input:principal.issuer": principals[1]!.issuer,
    "input:principal.subject": principals[1]!.subject,
  });
});

it("shows no-pending only after a successful terminal empty page", async () => {
  let complete!: (response: Response) => void;
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockImplementation(
      () =>
        new Promise((resolve) => {
          complete = resolve;
        }),
    ),
  );
  render(<AccessRequests available busy={false} onAdmit={vi.fn()} onDisclosureRefused={vi.fn()} />);
  expect(screen.queryByText("No pending access requests.")).toBeNull();
  await waitFor(() => {
    expect(complete).toBeTypeOf("function");
  });
  complete(reply({ ...document, screen: { regions: [] } }));
  await screen.findByText("No pending access requests.");
  expect(screen.queryByRole("table")).toBeNull();
});
it("retains earlier rows after an empty continuation and never calls the aggregate empty", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(reply({ ...document, nextCursor: "opaque-next-page" }))
    .mockResolvedValueOnce(reply({ ...document, screen: { regions: [] } }));
  vi.stubGlobal("fetch", fetch);
  render(<AccessRequests available busy={false} onAdmit={vi.fn()} onDisclosureRefused={vi.fn()} />);
  await screen.findByText("Core second caption Ω");
  await userEvent.click(screen.getByRole("button", { name: /more/iu }));
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  expect(screen.getByText("Core second caption Ω")).toBeVisible();
  expect(screen.queryByText("No pending access requests.")).toBeNull();
});
it("does not turn a failed first read into no pending requests", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockRejectedValue(new TypeError("offline")),
  );
  render(<AccessRequests available busy={false} onAdmit={vi.fn()} onDisclosureRefused={vi.fn()} />);
  await screen.findByRole("alert");
  expect(screen.queryByText("No pending access requests.")).toBeNull();
});
it("does not claim global emptiness while an empty successful page has a continuation", async () => {
  vi.stubGlobal(
    "fetch",
    vi
      .fn<typeof globalThis.fetch>()
      .mockResolvedValue(
        reply({ ...document, screen: { regions: [] }, nextCursor: "more-requests" }),
      ),
  );
  render(<AccessRequests available busy={false} onAdmit={vi.fn()} onDisclosureRefused={vi.fn()} />);
  await screen.findByRole("button", { name: /more/iu });
  expect(screen.queryByText("No pending access requests.")).toBeNull();
});
