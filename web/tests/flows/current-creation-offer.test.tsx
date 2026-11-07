import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { App } from "../../src/App";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { stubViewCounts } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { actorContextInput, workBasis, registration } from "../support/inputs";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
import { consumerContractIdentity, resourceUnavailableDiagnostic } from "@contracts/client";
import { preview as reviewed } from "../support/operation-hook.fixtures";

const contextReply = (
  offered: boolean,
  scopedRegistrar = false,
): ApiResult<ServiceResponse<"context">> => ({
  kind: "outcome",
  status: 200,
  value: {
    outcome: {
      view: { kind: "RECORD" as const, sections: [] },
      kind: "SUCCEEDED",
      data: {
        requester: actorContextInput({
          ...workBasis,
          productVersion: "synthetic-creation-offer",
          capabilities:
            offered || scopedRegistrar
              ? ["READ_CASE", "READ_HISTORY", "REGISTER"]
              : ["READ_CASE", "READ_HISTORY"],
          views: ["CASES", "OPERATIONS"],
          offers: offered ? [{ kind: "OPEN" }] : [],
          hasScopedCases: !offered && scopedRegistrar,
          roles: [
            {
              role: offered || scopedRegistrar ? "CASE_REGISTRAR" : "CASE_READER",
              installation: offered || !scopedRegistrar,
              caseCount: !offered && scopedRegistrar ? 1 : 0,
            },
          ],
        }),
      },
    },
  },
});
beforeEach(() => {
  vi.spyOn(service, "session").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        view: { kind: "RECORD" as const, sections: [] },
        kind: "SUCCEEDED",
        data: {
          authenticated: true,
          contractIdentity: consumerContractIdentity,
          antiforgeryToken: "current-offer-token",
        },
      },
    },
  });
  stubViewCounts();
  mockEndpoint("case.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        view: { kind: "RECORD" as const, sections: [] },
        kind: "SUCCEEDED",
        data: {
          basis: workBasis,
          items: [],
          offers: [{ kind: "OPEN" }],
          nextCursor: null,
          exactReference: null,
        },
      },
    },
  });
  mockEndpoint("case.summary").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        view: { kind: "RECORD" as const, sections: [] },
        kind: "SUCCEEDED",
        data: {
          basis: workBasis,
          counts: { total: 0, lifecycle: [], status: [], progress: [], assignment: [], due: [] },
          claimed: [],
          payable: [],
        },
      },
    },
  });
});
const freshContext = async (
  mock: ReturnType<typeof mockEndpoint<"context">>,
  offered: boolean,
  scopedRegistrar = false,
) => {
  const before = mock.mock.calls.length;
  mock.mockResolvedValue(contextReply(offered, scopedRegistrar));
  await act(async () => {
    window.dispatchEvent(new Event("focus"));
    await Promise.resolve();
  });
  await waitFor(() => {
    expect(mock).toHaveBeenCalledTimes(before + 1);
  });
  await act(() => Promise.resolve());
};
const openDraft = async () => {
  const user = userEvent.setup();
  await user.click(await screen.findByRole("link", { name: "Open a case" }));
  const reference = await screen.findByRole("textbox", { name: "Case reference" });
  await user.type(reference, "CURRENT-OFFER");
  const claimant = screen.getByRole("textbox", { name: "Claimant name" });
  await user.type(claimant, "Owned creation draft");
  const date = screen.getByRole("group", { name: "Incident date" });
  const day = date.querySelector<HTMLElement>('[data-type="day"]');
  if (day === null) {
    throw new Error("The real segmented date entry is required.");
  }
  await user.click(day);
  await user.keyboard("12");
  return { user, reference, claimant, day };
};
const completeRegistration = async (user: ReturnType<typeof userEvent.setup>) => {
  for (const [name, value] of Object.entries(registration())) {
    if (value === null) {
      continue;
    }
    const control = document.querySelector<HTMLElement>(`[data-field-name="${name}"]`);
    if (control === null) {
      throw new Error("An actual catalog field is required.");
    }
    if (name === "incidentDate" || name === "incidentNotificationDate") {
      const [year, month, day] = value.split("-");
      for (const [part, text] of [
        ["year", year!],
        ["month", month!],
        ["day", day!],
      ]) {
        const segment = control.querySelector<HTMLElement>(`[data-type="${part}"]`);
        if (segment === null) {
          throw new Error("The actual segmented calendar is required.");
        }
        await user.click(segment);
        await user.keyboard(text!);
      }
    } else {
      await user.clear(control);
      await user.type(control, value);
      if (control.getAttribute("role") === "combobox") {
        await user.tab();
      }
    }
  }
};
it.each([false, true])(
  "uses the current OPEN offer without discarding owned partial input; scoped registrar=%j",
  async (scopedRegistrar) => {
    const context = mockEndpoint("context").mockResolvedValue(contextReply(true));
    const preview = vi
      .spyOn(service, "preview")
      .mockImplementation((prepared) => Promise.resolve(reviewed(prepared)));
    const execute = vi.spyOn(service, "execute").mockResolvedValue({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "invalidInput" },
      dispatched: false,
    });
    render(<App />);
    const { user, reference, claimant, day } = await openDraft();
    const review = screen.getByRole("button", { name: "Review changes" });
    expect(review).not.toBeDisabled();
    await freshContext(context, false, scopedRegistrar);
    expect(reference).toBeVisible();
    expect(reference).toHaveValue("CURRENT-OFFER");
    expect(claimant).toHaveValue("Owned creation draft");
    expect(day).toHaveAttribute("aria-valuenow", "12");
    expect(review).toBeDisabled();
    await user.click(review);
    expect(preview).not.toHaveBeenCalled();
    expect(execute).not.toHaveBeenCalled();
    await freshContext(context, true);
    expect(screen.getByRole("textbox", { name: "Case reference" })).toHaveValue("CURRENT-OFFER");
    expect(
      screen.getByRole("group", { name: "Incident date" }).querySelector('[data-type="day"]'),
    ).toHaveAttribute("aria-valuenow", "12");
    expect(screen.getByRole("button", { name: "Review changes" })).not.toBeDisabled();
    expect(preview).not.toHaveBeenCalled();
    expect(execute).not.toHaveBeenCalled();
  },
);
it("disables a prepared OPEN confirmation when the current offer disappears, retaining reviewed identity", async () => {
  const context = mockEndpoint("context").mockResolvedValue(contextReply(true));
  const preview = vi
    .spyOn(service, "preview")
    .mockImplementation((prepared) => Promise.resolve(reviewed(prepared)));
  const execute = vi.spyOn(service, "execute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "invalidInput" },
    dispatched: false,
  });
  render(<App />);
  const { user } = await openDraft();
  await completeRegistration(user);
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(document.querySelector("button[data-confirm-operation]")).not.toBeNull();
  });
  const prepared = preview.mock.calls[0]?.[0];
  if (prepared === undefined) {
    throw new Error("An actual reviewed request is required.");
  }
  const confirm = screen.getByRole("button", { name: "Open a case" });
  expect(confirm).not.toBeDisabled();
  await freshContext(context, false);
  expect(confirm).toBeDisabled();
  await user.click(confirm);
  expect(execute).not.toHaveBeenCalled();
  expect(preview).toHaveBeenCalledOnce();
  expect(screen.getByRole("dialog")).toHaveTextContent(prepared.input.operationId);
});
it("does not restore masked creation entry from a fresh no-OPEN observation, then restores on fresh OPEN", async () => {
  const context = mockEndpoint("context").mockResolvedValue(contextReply(true));
  const preview = vi.spyOn(service, "preview").mockImplementation((prepared) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        operationId: prepared.input.operationId,
        outcome: { kind: "REJECTED", data: resourceUnavailableDiagnostic },
      },
    }),
  );
  const execute = vi.spyOn(service, "execute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "invalidInput" },
    dispatched: false,
  });
  render(<App />);
  const { user } = await openDraft();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(screen.queryByRole("group", { name: "Incident date" })).toBeNull();
  });
  await freshContext(context, false);
  expect(screen.queryByRole("group", { name: "Incident date" })).toBeNull();
  expect(preview).toHaveBeenCalledOnce();
  expect(execute).not.toHaveBeenCalled();
  await freshContext(context, true);
  const date = await screen.findByRole("group", { name: "Incident date" });
  await waitFor(() => {
    expect(date.querySelector('[data-type="day"]')).toHaveAttribute("aria-valuenow", "12");
  });
  expect(screen.getByRole("textbox", { name: "Case reference" })).toHaveValue("CURRENT-OFFER");
  expect(screen.getByRole("button", { name: "Review changes" })).not.toBeDisabled();
  expect(preview).toHaveBeenCalledOnce();
  expect(execute).not.toHaveBeenCalled();
});
it("retains editable local creation fields through Settings without enabling the unavailable OPEN act", async () => {
  const context = mockEndpoint("context").mockResolvedValue(contextReply(true));
  const preview = vi
    .spyOn(service, "preview")
    .mockImplementation((prepared) => Promise.resolve(reviewed(prepared)));
  const execute = vi.spyOn(service, "execute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "invalidInput" },
    dispatched: false,
  });
  render(<App />);
  const { user } = await openDraft();
  await freshContext(context, false);
  expect(screen.getByRole("button", { name: "Review changes" })).toBeDisabled();
  const claimant = screen.getByRole("textbox", { name: "Claimant name" });
  expect(claimant).not.toBeDisabled();
  await user.type(claimant, " revised{Enter}");
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Settings" }));
  await screen.findByRole("heading", { name: "Settings", level: 1 });
  await user.click(screen.getByRole("button", { name: "Return to workspace" }));
  const date = await screen.findByRole("group", { name: "Incident date" });
  await waitFor(() => {
    expect(date.querySelector('[data-type="day"]')).toHaveAttribute("aria-valuenow", "12");
  });
  expect(document.querySelector('input[name="act.values.incidentDate"]')).toHaveValue("");
  expect(screen.getByRole("textbox", { name: "Case reference" })).toHaveValue("CURRENT-OFFER");
  expect(screen.getByRole("textbox", { name: "Claimant name" })).toHaveValue(
    "Owned creation draft revised",
  );
  expect(screen.getByRole("button", { name: "Review changes" })).toBeDisabled();
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
  await freshContext(context, true);
  expect(
    screen.getByRole("group", { name: "Incident date" }).querySelector('[data-type="day"]'),
  ).toHaveAttribute("aria-valuenow", "12");
  expect(document.querySelector('input[name="act.values.incidentDate"]')).toHaveValue("");
  expect(screen.getByRole("button", { name: "Review changes" })).not.toBeDisabled();
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});
it("keeps creation authoring locked during preparation, submission and an unknown outcome", async () => {
  const context = mockEndpoint("context").mockResolvedValue(contextReply(true));
  const preparing = Promise.withResolvers<Awaited<ReturnType<typeof service.preview>>>();
  const submitting = Promise.withResolvers<Awaited<ReturnType<typeof service.execute>>>();
  const preview = vi.spyOn(service, "preview").mockReturnValue(preparing.promise);
  const execute = vi.spyOn(service, "execute").mockReturnValue(submitting.promise);
  render(<App />);
  const { user } = await openDraft();
  await completeRegistration(user);
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  expect(screen.getByRole("textbox", { name: "Claimant name" })).toBeDisabled();
  await freshContext(context, false);
  expect(screen.getByRole("textbox", { name: "Claimant name" })).toBeDisabled();
  const prepared = preview.mock.calls[0]?.[0];
  if (prepared === undefined) {
    throw new Error("An actual prepared request is required.");
  }
  await act(async () => {
    preparing.resolve(reviewed(prepared));
    await preparing.promise;
  });
  await freshContext(context, true);
  const confirm = await screen.findByRole("button", { name: "Open a case" });
  await user.click(confirm);
  expect(execute).toHaveBeenCalledOnce();
  expect(screen.queryByRole("textbox", { name: "Claimant name" })).toBeNull();
  await act(async () => {
    submitting.resolve({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    });
    await submitting.promise;
  });
  await waitFor(() => {
    expect(screen.getByRole("dialog")).toHaveTextContent("unknown");
  });
  await freshContext(context, false);
  expect(screen.queryByRole("textbox", { name: "Claimant name" })).toBeNull();
  expect(preview).toHaveBeenCalledOnce();
  expect(execute).toHaveBeenCalledOnce();
});
