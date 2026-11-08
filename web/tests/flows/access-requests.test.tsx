import { presentedOutcome } from "../support/read-view.fixtures";
import { act, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import type { AccessRequest, AccessRequestPage } from "@contracts/client";
import { AccessRequests } from "../../src/views/authority/AccessRequests";
import { mockEndpoint } from "../support/endpoint-mocks";
import { render, screen, waitFor, TestSession } from "../support/presentation-test-support";
import { workBasis } from "../support/inputs";
import type { ApiResult, ServiceResponse } from "../../src/api/service";
const request: AccessRequest = {
  principal: { kind: "SERVICE", issuer: "https://issuer.example/", subject: "shared-subject" },
  firstSeen: "2026-11-01T10:00:00.000000Z",
  lastSeen: "2026-11-02T12:00:00.000000Z",
  claims: { name: "Unverified hint", email: "hint@example.test" },
};
const succeeded = (
  items: ReadonlyArray<AccessRequest>,
  nextCursor: string | null = null,
): ApiResult<ServiceResponse<"authority.requests">> => ({
  kind: "outcome",
  status: 200,
  value: {
    outcome: presentedOutcome("authority.requests", "SUCCEEDED", {
      basis: workBasis,
      items,
      nextCursor,
    } satisfies AccessRequestPage),
  },
});
const setup = (available = true, busy = false) => {
  const onAdmit = vi.fn();
  const onDisclosureRefused = vi.fn();
  const props = { available, busy, onAdmit, onDisclosureRefused };
  const view = render(<AccessRequests {...props} />);
  return { ...view, props, onAdmit, onDisclosureRefused };
};
it("never reads requests without the supplied disclosure gate", async () => {
  const read = mockEndpoint("authority.requests");
  setup(false);
  await act(() => Promise.resolve());
  expect(read).not.toHaveBeenCalled();
  expect(screen.queryByRole("heading", { name: "Access requests" })).toBeNull();
});
it("pages with the actual cursor and binds admission to the exact principal and hints", async () => {
  const read = mockEndpoint("authority.requests");
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
  expect(onAdmit).toHaveBeenCalledExactlyOnceWith(request);
});
it("requires dismiss confirmation and sends the actual principal kind, issuer and subject", async () => {
  mockEndpoint("authority.requests").mockResolvedValue(succeeded([request]));
  const dismiss = mockEndpoint("authority.requests.dismiss");
  dismiss.mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: presentedOutcome("authority.requests.dismiss", "SUCCEEDED", null),
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
it("keeps a retained authority draft from being overwritten or dismissed", async () => {
  mockEndpoint("authority.requests").mockResolvedValue(succeeded([request]));
  const { onAdmit } = setup(true, true);
  await screen.findByText("Unverified hint");
  expect(screen.getByRole("button", { name: "Add person" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Dismiss access request" })).toBeDisabled();
  expect(onAdmit).not.toHaveBeenCalled();
});
it("clears disclosed requests when availability is lost and ignores a late read", async () => {
  let resolve!: (result: ApiResult<ServiceResponse<"authority.requests">>) => void;
  const read = mockEndpoint("authority.requests").mockImplementation(
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
  mockEndpoint("authority.requests")
    .mockResolvedValueOnce(succeeded([request]))
    .mockResolvedValue(succeeded([]));
  let resolve!: (result: ApiResult<ServiceResponse<"authority.requests.dismiss">>) => void;
  const dismiss = mockEndpoint("authority.requests.dismiss").mockImplementation(
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
        outcome: { view: { kind: "RECORD" as const, sections: [] }, kind: "SUCCEEDED", data: null },
      },
    });
    await Promise.resolve();
  });
  expect(screen.queryByText("Access request dismissed.")).toBeNull();
});
it("does not retry an uncertain dismissal or remove its row optimistically", async () => {
  mockEndpoint("authority.requests").mockResolvedValue(succeeded([request]));
  const dismiss = mockEndpoint("authority.requests.dismiss").mockResolvedValue({
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
  mockEndpoint("authority.requests").mockResolvedValue(succeeded([request]));
  mockEndpoint("authority.requests.dismiss").mockResolvedValue({
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
  let resolve!: (result: ApiResult<ServiceResponse<"authority.requests">>) => void;
  const read = mockEndpoint("authority.requests").mockResolvedValueOnce(
    succeeded([request], "next"),
  );
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
