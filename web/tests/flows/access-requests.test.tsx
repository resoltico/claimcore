import type { Principal, Affordance, RegionRow } from "@contracts/client";
import { operationAffordance } from "../support/affordances.fixtures";
import { humanReadReply } from "../support/human-read-reply.fixtures";
import { act, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";

import { AccessRequests } from "../../src/views/people/AccessRequests";
import { mockEndpoint } from "../support/endpoint-mocks";
import { render, screen, waitFor, TestSession } from "../support/presentation-test-support";
import { workBasis } from "../support/inputs";
import type { ApiResult, ServiceResponse } from "../../src/api/service";
type RequestFixture = {
  key: string;
  principal: Principal;
  profileSeed: { displayName: string; contact: string | null };
  row: RegionRow;
};
const request: RequestFixture = {
  key: "0",
  principal: { kind: "SERVICE", issuer: "https://issuer.example/", subject: "shared-subject" },
  profileSeed: { displayName: "Unverified hint", contact: "hint@example.test" },
  row: {
    key: "0",
    values: [
      { kind: "TEXT", value: "Unverified hint" },
      { kind: "TEXT", value: "hint@example.test" },
      { kind: "CODE", vocabulary: "principalKind", token: "SERVICE" },
      { kind: "INSTANT", value: "2026-11-01T10:00:00.000000Z" },
      { kind: "INSTANT", value: "2026-11-02T12:00:00.000000Z" },
    ],
  },
};
const succeeded = (
  items: ReadonlyArray<RequestFixture>,
  nextCursor: string | null = null,
): ApiResult<ServiceResponse<"people.requests">> => ({
  kind: "outcome",
  status: 200,
  value: humanReadReply("people.requests", {
    screen: {
      regions: [
        {
          role: "RESULTS",
          content: {
            kind: "TABLE",
            columns: [
              {
                key: "name",
                labelId: "label.requestedName",
                prominence: "PRIMARY",
                isMoney: false,
              },
              {
                key: "email",
                labelId: "field.profile.contact.label",
                prominence: "PRIMARY",
                isMoney: false,
              },
              {
                key: "kind",
                labelId: "field.principal.kind.label",
                prominence: "SECONDARY",
                isMoney: false,
              },
              { key: "first", labelId: "label.firstSeen", prominence: "SECONDARY", isMoney: false },
              { key: "last", labelId: "label.lastSeen", prominence: "SECONDARY", isMoney: false },
            ],
            rows: items.map((item) => ({
              ...item.row,
              actions: [
                operationAffordance(
                  "people.admit",
                  { kind: "PEOPLE" },
                  {
                    "input:principal.kind": item.principal.kind,
                    "input:principal.issuer": item.principal.issuer,
                    "input:principal.subject": item.principal.subject,
                    "input:profile.displayName": item.profileSeed.displayName,
                    "input:profile.contact": item.profileSeed.contact,
                  },
                  ["input:profile.displayName", "input:profile.contact", "input:grants"],
                ),
                {
                  task: "people.requests.dismiss",
                  binding: { kind: "PEOPLE" },
                  seeds: {
                    "input:principal.kind": item.principal.kind,
                    "input:principal.issuer": item.principal.issuer,
                    "input:principal.subject": item.principal.subject,
                  },
                  editable: [],
                  choices: {},
                  primary: false,
                  blocked: null,
                } satisfies Affordance,
              ],
            })),
          },
        },
      ],
    },
    subject: { kind: "LIST" },
    actions: [],
    links: [],
    history: null,
    nextCursor,
    basis: { kind: "SNAPSHOT", observation: workBasis },
    disclosure: "CURRENT",
  }),
});
const setup = (available = true, busy = false) => {
  const onAdmit = vi.fn();
  const onDisclosureRefused = vi.fn();
  const props = { available, busy, onAdmit, onDisclosureRefused };
  const view = render(<AccessRequests {...props} />);
  return { ...view, props, onAdmit, onDisclosureRefused };
};
it("never reads requests without the supplied disclosure gate", async () => {
  const read = mockEndpoint("people.requests");
  setup(false);
  await act(() => Promise.resolve());
  expect(read).not.toHaveBeenCalled();
  expect(screen.queryByRole("heading", { name: "Access requests" })).toBeNull();
});
it("pages with the actual cursor and binds admission to the exact principal and hints", async () => {
  const read = mockEndpoint("people.requests");
  read.mockResolvedValueOnce(succeeded([request], "next-request-page"));
  read.mockResolvedValueOnce(
    succeeded([{ ...request, principal: { ...request.principal, kind: "HUMAN" } }]),
  );
  const { onAdmit } = setup();
  await screen.findByText("Unverified hint");
  expect(screen.getByRole("columnheader", { name: "Requested name" })).toBeVisible();
  expect(onAdmit).not.toHaveBeenCalled();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Load more access requests" }));
  await waitFor(() => {
    expect(screen.getAllByText("Unverified hint")).toHaveLength(2);
  });
  expect(read.mock.calls.map((call) => call[1])).toEqual([
    { limit: 50 },
    { limit: 50, cursor: "next-request-page" },
  ]);
  await user.click(screen.getAllByRole("button", { name: "Add person" })[0]!);
  expect(onAdmit).toHaveBeenCalledExactlyOnceWith(
    expect.objectContaining({
      task: "people.admit",
      binding: { kind: "PEOPLE" },
      seeds: {
        "input:principal.kind": "SERVICE",
        "input:principal.issuer": request.principal.issuer,
        "input:principal.subject": request.principal.subject,
        "input:profile.displayName": "Unverified hint",
        "input:profile.contact": "hint@example.test",
      },
    }),
  );
  onAdmit.mockClear();
  await user.click(screen.getAllByRole("button", { name: "Add person" })[1]!);
  expect(onAdmit).toHaveBeenCalledExactlyOnceWith(
    expect.objectContaining({
      task: "people.admit",
      seeds: {
        "input:principal.kind": "HUMAN",
        "input:principal.issuer": request.principal.issuer,
        "input:principal.subject": request.principal.subject,
        "input:profile.displayName": "Unverified hint",
        "input:profile.contact": "hint@example.test",
      },
    }),
  );
});
it("requires dismiss confirmation and sends the actual principal kind, issuer and subject", async () => {
  mockEndpoint("people.requests").mockResolvedValue(succeeded([request]));
  const dismiss = mockEndpoint("people.requests.dismiss");
  dismiss.mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadReply("people.requests.dismiss", {
        screen: { regions: [] },
        subject: { kind: "LIST" },
        actions: [],
        links: [],
        history: null,
        nextCursor: null,
        basis: { kind: "UNAVAILABLE" },
        disclosure: "NONE",
      }),
    },
  });
  setup();
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Dismiss access request" }));
  let dialog = screen.getByRole("dialog", { name: "Dismiss access request" });
  expect(dismiss).not.toHaveBeenCalled();
  await user.click(within(dialog).getByRole("button", { name: "Close" }));
  expect(dismiss).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Dismiss access request" }));
  dialog = screen.getByRole("dialog", { name: "Dismiss access request" });
  await user.click(within(dialog).getByRole("button", { name: "Dismiss access request" }));
  await screen.findByText("Access request dismissed.");
  expect(dismiss).toHaveBeenCalledExactlyOnceWith(
    "token",
    request.principal,
    expect.any(AbortSignal),
  );
});
it("keeps a retained people draft from being overwritten or dismissed", async () => {
  mockEndpoint("people.requests").mockResolvedValue(succeeded([request]));
  const { onAdmit } = setup(true, true);
  await screen.findByText("Unverified hint");
  expect(screen.getByRole("button", { name: "Add person" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Dismiss access request" })).toBeDisabled();
  expect(onAdmit).not.toHaveBeenCalled();
});
it("clears disclosed requests when availability is lost and ignores a late read", async () => {
  let resolve!: (result: ApiResult<ServiceResponse<"people.requests">>) => void;
  const read = mockEndpoint("people.requests").mockImplementation(
    () =>
      new Promise((done) => {
        resolve = done;
      }),
  );
  const { rerender, props } = setup();
  await waitFor(() => {
    expect(read).toHaveBeenCalledOnce();
  });
  rerender(<AccessRequests {...props} available={false} />);
  await act(async () => {
    resolve(succeeded([request]));
    await Promise.resolve();
  });
  expect(screen.queryByText("Unverified hint")).toBeNull();
});
it("aborts dismissal and forgets request hints on a session change", async () => {
  mockEndpoint("people.requests")
    .mockResolvedValueOnce(succeeded([request]))
    .mockResolvedValue(succeeded([]));
  let resolve!: (result: ApiResult<ServiceResponse<"people.requests.dismiss">>) => void;
  const dismiss = mockEndpoint("people.requests.dismiss").mockImplementation(
    () =>
      new Promise((done) => {
        resolve = done;
      }),
  );
  const props = { available: true, busy: false, onAdmit: vi.fn(), onDisclosureRefused: vi.fn() };
  const { rerender } = render(
    <TestSession credential="first-session">
      <AccessRequests {...props} />
    </TestSession>,
  );
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Dismiss access request" }));
  await user.click(
    within(screen.getByRole("dialog")).getByRole("button", { name: "Dismiss access request" }),
  );
  rerender(
    <TestSession credential="other-session">
      <AccessRequests {...props} />
    </TestSession>,
  );
  expect(dismiss.mock.calls[0]?.[2]?.aborted).toBe(true);
  await act(async () => {
    resolve({
      kind: "outcome",
      status: 200,
      value: {
        ...humanReadReply("people.requests.dismiss", {
          screen: { regions: [] },
          subject: { kind: "LIST" },
          actions: [],
          links: [],
          history: null,
          nextCursor: null,
          basis: { kind: "UNAVAILABLE" },
          disclosure: "NONE",
        }),
      },
    });
    await Promise.resolve();
  });
  expect(screen.queryByText("Access request dismissed.")).toBeNull();
});
it("does not retry an uncertain dismissal or remove its row optimistically", async () => {
  mockEndpoint("people.requests").mockResolvedValue(succeeded([request]));
  const dismiss = mockEndpoint("people.requests.dismiss").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "responseTimeout" },
    dispatched: true,
  });
  setup();
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Dismiss access request" }));
  await user.click(
    within(screen.getByRole("dialog")).getByRole("button", { name: "Dismiss access request" }),
  );
  await waitFor(() => {
    expect(screen.queryByRole("dialog")).toBeNull();
  });
  expect(dismiss).toHaveBeenCalledOnce();
  expect(screen.getByText("Unverified hint")).toBeVisible();
  expect(screen.getByRole("alert")).toBeVisible();
});
it("clears requests immediately on a dismissal disclosure refusal", async () => {
  mockEndpoint("people.requests").mockResolvedValue(succeeded([request]));
  mockEndpoint("people.requests.dismiss").mockResolvedValue({
    kind: "hostFailure",
    status: 401,
    failure: { code: "SESSION_REJECTED", phase: "NOT_STARTED" },
  });
  const { onDisclosureRefused } = setup();
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Dismiss access request" }));
  await user.click(
    within(screen.getByRole("dialog")).getByRole("button", { name: "Dismiss access request" }),
  );
  await waitFor(() => {
    expect(onDisclosureRefused).toHaveBeenCalledOnce();
  });
  expect(screen.queryByText("Unverified hint")).toBeNull();
});

it("clears an open dismissal dialog on a paging disclosure refusal before a parent update", async () => {
  let resolve!: (result: ApiResult<ServiceResponse<"people.requests">>) => void;
  const read = mockEndpoint("people.requests").mockResolvedValueOnce(succeeded([request], "next"));
  read.mockImplementationOnce(
    () =>
      new Promise((done) => {
        resolve = done;
      }),
  );
  const { onDisclosureRefused } = setup();
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Load more access requests" }));
  await user.click(screen.getByRole("button", { name: "Dismiss access request" }));
  expect(screen.getByRole("dialog")).toBeVisible();
  await act(async () => {
    resolve({
      kind: "hostFailure",
      status: 401,
      failure: { code: "SESSION_REJECTED", phase: "NOT_STARTED" },
    });
    await Promise.resolve();
  });
  await waitFor(() => {
    expect(onDisclosureRefused).toHaveBeenCalledOnce();
  });
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(screen.queryByText("Unverified hint")).toBeNull();
});

it("reviews and dismisses the selected page-local request without reusing an earlier caption or principal", async () => {
  const later: RequestFixture = {
    ...request,
    principal: { ...request.principal, kind: "HUMAN" },
    row: {
      ...request.row,
      values: [{ kind: "TEXT", value: "LATER_REQUEST_CAPTION" }, ...request.row.values.slice(1)],
    },
  };
  mockEndpoint("people.requests")
    .mockResolvedValueOnce(succeeded([request], "next"))
    .mockResolvedValueOnce(succeeded([later]))
    .mockResolvedValueOnce(succeeded([]));
  const dismiss = mockEndpoint("people.requests.dismiss").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("people.requests.dismiss", {
      screen: { regions: [] },
      subject: { kind: "LIST" },
      actions: [],
      links: [],
      history: null,
      nextCursor: null,
      basis: { kind: "UNAVAILABLE" },
      disclosure: "NONE",
    }),
  });
  setup();
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Load more access requests" }));
  await screen.findByText("LATER_REQUEST_CAPTION");
  await user.click(screen.getAllByRole("button", { name: "Dismiss access request" })[1]!);
  const dialog = screen.getByRole("dialog");
  expect(dialog).toHaveTextContent("LATER_REQUEST_CAPTION");
  expect(dialog).not.toHaveTextContent("Unverified hint");
  await user.click(within(dialog).getByRole("button", { name: "Dismiss access request" }));
  await waitFor(() => {
    expect(dismiss).toHaveBeenCalledOnce();
  });
  expect(dismiss.mock.calls[0]?.[1]).toEqual(later.principal);
});
