import { operationAffordance } from "../../../support/affordances.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen, waitFor, fireEvent } from "../../../support/presentation-test-support";
import { humanReadHttpReply } from "../../../support/human-read-reply.fixtures";
import {
  requestText,
  requestAddress,
  endpointResponse,
} from "../../../support/transport-response.fixtures";
import { SelectedReminder } from "../../../../src/features/work/SelectedReminder";
import type { HumanDocument } from "@contracts/client";
afterEach(() => vi.unstubAllGlobals());
it("reads the exact terminal reminder directly without scanning a pending list", async () => {
  const document: HumanDocument = {
    subject: { kind: "LIST" },
    screen: {
      regions: [
        {
          role: "FACTS",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "exact",
                labelId: "field.reminderAction.label",
                prominence: "PRIMARY",
                value: { kind: "TEXT", value: "Completed outside the first page" },
              },
            ],
          },
        },
      ],
    },
    actions: [],
    links: [],
    history: null,
    nextCursor: null,
    disclosure: "CURRENT",
    basis: {
      kind: "SNAPSHOT",
      observation: {
        observedAt: "2026-11-09T12:00:00.000000Z",
        businessDate: "2026-11-09",
        timeZone: "Etc/UTC",
      },
    },
  };
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValue(humanReadHttpReply("reminder.get", document));
  vi.stubGlobal("fetch", fetch);
  const target = {
    kind: "REFERENCE" as const,
    caseReference: "00000000-0000-4000-8000-000000000001",
  };
  render(
    <SelectedReminder
      target={target}
      reminderId="00000000-0000-4000-8000-000000000099"
      onAct={vi.fn()}
      onDisclosureRefused={vi.fn()}
    />,
  );
  await screen.findByText("Completed outside the first page");
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(requestAddress(fetch.mock.calls[0]![0])).toMatch(/\/api\/reminder\.get$/u);
  expect(JSON.parse(requestText(fetch.mock.calls[0]![1]?.body))).toEqual({
    target,
    reminderId: "00000000-0000-4000-8000-000000000099",
  });
});

it("revalidates selected disclosure with its parent and bounds repeated refusal notifications", async () => {
  const refusal = () =>
    endpointResponse("reminder.get", {
      representation: "HUMAN",
      outcome: {
        kind: "REJECTED",
        rejection: {
          kind: "REFUSED",
          advice: {
            message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
            action: "ASK_FOR_ACCESS",
          },
        },
      },
    });
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockImplementation(() => Promise.resolve(refusal()));
  vi.stubGlobal("fetch", fetch);
  const onRefused = vi.fn();
  const target = { kind: "REFERENCE" as const, caseReference: "CC-55KW-SSJ4-8HMS-G1BH-73" };
  const view = render(
    <SelectedReminder
      target={target}
      reminderId="00000000-0000-4000-8000-000000000099"
      observation="first"
      onAct={vi.fn()}
      onDisclosureRefused={onRefused}
    />,
  );
  await waitFor(() => {
    expect(onRefused).toHaveBeenCalledTimes(1);
  });
  view.rerender(
    <SelectedReminder
      target={target}
      reminderId="00000000-0000-4000-8000-000000000099"
      observation="parent-revalidated"
      onAct={vi.fn()}
      onDisclosureRefused={onRefused}
    />,
  );
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  expect(onRefused).toHaveBeenCalledTimes(1);
  expect(screen.queryByRole("button", { name: "Try again" })).toBeNull();
});

it("masks an old selected observation until the refreshed parent-bound read completes", async () => {
  const document = (value: string): HumanDocument => ({
    subject: { kind: "LIST" },
    screen: {
      regions: [
        {
          role: "FACTS",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "exact",
                labelId: "field.reminderAction.label",
                prominence: "PRIMARY",
                value: { kind: "TEXT", value },
              },
            ],
          },
        },
      ],
    },
    actions: [],
    links: [],
    history: null,
    nextCursor: null,
    disclosure: "CURRENT",
    basis: {
      kind: "SNAPSHOT",
      observation: {
        observedAt: "2026-11-09T12:00:00.000000Z",
        businessDate: "2026-11-09",
        timeZone: "Etc/UTC",
      },
    },
  });
  const pending = Promise.withResolvers<Response>();
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(humanReadHttpReply("reminder.get", document("Old reminder observation")))
    .mockReturnValueOnce(pending.promise);
  vi.stubGlobal("fetch", fetch);
  const props = {
    target: { kind: "REFERENCE" as const, caseReference: "CC-55KW-SSJ4-8HMS-G1BH-73" },
    reminderId: "00000000-0000-4000-8000-000000000099",
    onAct: vi.fn(),
    onDisclosureRefused: vi.fn(),
  };
  const view = render(<SelectedReminder {...props} observation="before-role-refresh" />);
  await screen.findByText("Old reminder observation");
  view.rerender(<SelectedReminder {...props} observation="after-role-refresh" />);
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  expect(screen.queryByText("Old reminder observation")).toBeNull();
  pending.resolve(humanReadHttpReply("reminder.get", document("Fresh reminder observation")));
  await screen.findByText("Fresh reminder observation");
});

it("offers explicit retry for a busy exact read without dispatching an operation", async () => {
  const document: HumanDocument = {
    subject: { kind: "LIST" },
    screen: { regions: [] },
    actions: [],
    links: [],
    history: null,
    nextCursor: null,
    disclosure: "CURRENT",
    basis: {
      kind: "SNAPSHOT",
      observation: {
        observedAt: "2026-11-09T12:00:00.000000Z",
        businessDate: "2026-11-09",
        timeZone: "Etc/UTC",
      },
    },
  };
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(
      endpointResponse(
        "reminder.get",
        { hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } },
        429,
      ),
    )
    .mockResolvedValueOnce(humanReadHttpReply("reminder.get", document));
  vi.stubGlobal("fetch", fetch);
  const target = { kind: "REFERENCE" as const, caseReference: "CC-8DST-MHVK-1G6A-X08R-89" };
  const reminderId = "00000000-0000-4000-8000-000000000099";
  const onAct = vi.fn();
  const onDisclosureRefused = vi.fn();
  render(
    <SelectedReminder
      target={target}
      reminderId={reminderId}
      onAct={onAct}
      onDisclosureRefused={onDisclosureRefused}
    />,
  );
  const retry = await screen.findByRole("button", { name: "Try again" });
  expect(fetch).toHaveBeenCalledTimes(1);
  fireEvent.click(retry);
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(screen.queryByRole("alert")).toBeNull();
  });
  for (const [url, options] of fetch.mock.calls) {
    expect(requestAddress(url)).toMatch(/\/api\/reminder\.get$/u);
    expect(JSON.parse(requestText(options?.body))).toEqual({ target, reminderId });
  }
  expect(onAct).not.toHaveBeenCalled();
  expect(onDisclosureRefused).not.toHaveBeenCalled();
  expect(screen.queryByRole("button", { name: "Try again" })).toBeNull();
});

it("renders top-level exact reminder controls with their supplied binding and blocked state", async () => {
  const action = operationAffordance(
    "reminder.reopen",
    {
      kind: "REMINDER",
      target: { kind: "ID", caseId: "00000000-0000-4000-8000-000000000001" },
      reminderId: "00000000-0000-4000-8000-000000000099",
      expectedRevision: 7,
    },
    { "input:reminderDueDate": "2026-11-15" },
    ["input:reminderDueDate"],
    {},
  );
  const blocked = {
    ...operationAffordance("reminder.cancel", action.binding),
    blocked: {
      action: "ASK_FOR_ACCESS" as const,
      message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE" as const, arguments: [] },
    },
  };
  const document: HumanDocument = {
    subject: { kind: "LIST" },
    screen: { regions: [] },
    actions: [action, blocked],
    links: [],
    history: null,
    nextCursor: null,
    disclosure: "CURRENT",
    basis: {
      kind: "SNAPSHOT",
      observation: {
        observedAt: "2026-11-09T12:00:00.000000Z",
        businessDate: "2026-11-09",
        timeZone: "Etc/UTC",
      },
    },
  };
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(humanReadHttpReply("reminder.get", document)));
  const onAct = vi.fn();
  render(
    <SelectedReminder
      target={action.binding.target}
      reminderId={action.binding.reminderId}
      onAct={onAct}
      onDisclosureRefused={vi.fn()}
    />,
  );
  const reopen = await screen.findByRole("button", { name: /^Reopen reminder/u });
  expect(reopen).toHaveAttribute(
    "id",
    `selected-reminder-${action.binding.reminderId}-reminder.reopen`,
  );
  fireEvent.click(reopen);
  expect(onAct).toHaveBeenCalledWith(
    action,
    `selected-reminder-${action.binding.reminderId}-reminder.reopen`,
  );
  expect(screen.getByRole("button", { name: /^Cancel reminder/u })).toBeDisabled();
});
