import { authorityPreview } from "../support/authority-operation.fixtures";
import { installationOffers } from "../support/installation-advice.fixtures";
import { act, fireEvent, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { AuthorityPanel } from "../../src/views/AuthorityPanel";
import { AuthorityGrants } from "../../src/views/AuthorityGrants";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
import { wireReply } from "../support/contract-corpus.fixtures";
const first = "10000000-0000-4000-8000-000000000001";
const second = "10000000-0000-4000-8000-000000000002";
const actor = (actorId: string, prefix = "header") => ({
  actorId,
  principal: {
    kind: "SERVICE" as const,
    issuer: "https://issuer.example/",
    subject: `${prefix}-${actorId}`,
  },
  enabled: false,
});
const grants = (
  actorId: string,
  nextCursor: string | null = null,
  reference = `GRANT-${actorId}`,
): ApiResult<ServiceResponse<"authority.grants">> => ({
  kind: "outcome",
  status: 200,
  value: {
    outcome: {
      kind: "SUCCEEDED",
      data: {
        actor: actor(actorId),
        items: [{ role: "CASE_READER", scope: { kind: "CASE", caseReference: reference } }],
        nextCursor,
      },
    },
  },
});
const pending = <T,>() => {
  let finish: (value: T) => void = () => undefined;
  const promise = new Promise<T>((resolve) => {
    finish = resolve;
  });
  return { promise, finish };
};
const reads = (offered = true) => {
  const context = wireReply("context");
  if (context.outcome.kind !== "SUCCEEDED") {
    throw new Error("Context fixture absent.");
  }
  vi.spyOn(service, "context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...context,
      outcome: {
        kind: "SUCCEEDED",
        data: {
          ...context.outcome.data,
          offers: installationOffers(["REGISTER"]),
          views: offered ? ["AUTHORITY"] : [],
        },
      },
    },
  });
  vi.spyOn(service, "authorityList").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "SUCCEEDED",
        data: {
          items: [actor(first, "listed"), actor(second, "listed")],
          nextCursor: null,
        },
      },
    },
  });
};
beforeEach(() => {
  authorityPreview();
});
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});
it("loads no grants without explicit core-offered selection and discards a late prior target", async () => {
  reads();
  const old = pending<Awaited<ReturnType<typeof service.authorityGrants>>>();
  const request = vi
    .spyOn(service, "authorityGrants")
    .mockImplementationOnce(() => old.promise)
    .mockResolvedValueOnce(grants(second));
  render(<AuthorityPanel token="token" onLockChange={vi.fn()} />);
  const buttons = await screen.findAllByRole("button", { name: "View grants" });
  expect(request).not.toHaveBeenCalled();
  fireEvent.click(buttons[0]!);
  await waitFor(() => {
    expect(request).toHaveBeenCalledTimes(1);
  });
  fireEvent.click(buttons[1]!);
  await screen.findByText(`GRANT-${second}`);
  expect(request.mock.calls[0]?.[3]?.aborted).toBe(true);
  await act(() =>
    Promise.resolve().then(() => {
      old.finish(grants(first));
    }),
  );
  expect(screen.queryByText(`GRANT-${first}`)).toBeNull();
  expect(screen.getByText(`header-${second}`)).toBeVisible();
  expect(screen.getByRole("heading", { name: "Actor grants" })).toHaveFocus();
});
it("consumes the core read offer rather than inferring it from actor rows", async () => {
  reads(false);
  const request = vi.spyOn(service, "authorityGrants");
  render(<AuthorityPanel token="token" onLockChange={vi.fn()} />);
  await screen.findByText(`listed-${first}`);
  expect(screen.queryByRole("button", { name: "View grants" })).toBeNull();
  expect(request).not.toHaveBeenCalled();
});
it("keeps only the same actor's page on a transient error and follows server continuations", async () => {
  const request = vi
    .spyOn(service, "authorityGrants")
    .mockResolvedValueOnce(grants(first, "next"))
    .mockResolvedValueOnce({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    })
    .mockResolvedValueOnce(grants(first, null, "SECOND-PAGE"));
  render(
    <AuthorityGrants
      actorId={first}
      token="token"
      onBack={vi.fn()}
      onDisclosureRefused={vi.fn()}
    />,
  );
  fireEvent.click(await screen.findByRole("button", { name: "More grants" }));
  await screen.findByRole("alert");
  expect(screen.getByText(`GRANT-${first}`)).toBeVisible();
  fireEvent.click(screen.getByRole("button", { name: "More grants" }));
  await screen.findByText("All grants returned for this review are shown.");
  expect(request.mock.calls.map((call) => call[1])).toEqual([null, "next", "next"]);
});

const prepare = async () => {
  await waitFor(() => {
    expect(document.querySelector('[name="target.subject"]')).not.toBeNull();
  });
  fireEvent.change(document.querySelector('[name="target.issuer"]')!, {
    target: { value: "https://issuer.example/" },
  });
  fireEvent.change(document.querySelector('[name="target.subject"]')!, {
    target: { value: "authored-subject" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  fireEvent.click(await screen.findByRole("checkbox"));
};
const refused = (kind: string): ApiResult<ServiceResponse<"authority.grants">> =>
  kind === "access"
    ? {
        kind: "outcome",
        status: 200,
        value: {
          outcome: {
            kind: "REJECTED",
            data: {
              code: "RESOURCE_UNAVAILABLE",
              action: "NONE_REQUIRED",
            },
          },
        },
      }
    : {
        kind: "hostFailure",
        status: 401,
        failure: { code: "SESSION_REJECTED", phase: "NOT_STARTED" },
      };
it.each(["session", "access"])(
  "clears all selected disclosures on %s refusal while preserving exact uncertain mutation bytes",
  async (kind) => {
    reads();
    const grant = vi.spyOn(service, "authorityGrants").mockResolvedValue(refused(kind));
    const execute = vi.spyOn(service, "execute").mockResolvedValue({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    });
    render(<AuthorityPanel token="token" onLockChange={vi.fn()} />);
    await prepare();
    fireEvent.click(screen.getByRole("button", { name: "Record changes" }));
    await screen.findByRole("button", { name: "Send again" });
    const original = execute.mock.calls[0]?.[0];
    fireEvent.click(screen.getAllByRole("button", { name: "View grants" })[0]!);
    await waitFor(() => {
      expect(service.authorityList).toHaveBeenCalledTimes(2);
    });
    expect(screen.queryByRole("heading", { name: "Actor grants" })).toBeNull();
    expect(screen.queryByText(`GRANT-${first}`)).toBeNull();
    expect(grant).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole("button", { name: "Send again" }));
    await waitFor(() => {
      expect(execute).toHaveBeenCalledTimes(2);
    });
    expect(execute.mock.calls[1]?.[0]).toBe(original);
    expect(execute.mock.calls[1]?.[0].bytes).toBe(original?.bytes);
  },
);
it("ignores an in-flight grant page after an accepted authority mutation resets selection", async () => {
  reads();
  const old = pending<Awaited<ReturnType<typeof service.authorityGrants>>>();
  const request = vi.spyOn(service, "authorityGrants").mockReturnValue(old.promise);
  vi.spyOn(service, "execute").mockImplementation((prepared) => {
    const receipt = wireReply("operation.execute", "AUTHORITY");
    if (receipt.outcome.kind !== "ACCEPTED" || receipt.outcome.data.subject.kind !== "AUTHORITY") {
      throw new Error("Receipt fixture absent.");
    }
    return Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        ...receipt,
        operationId: prepared.input.operationId,
        outcome: {
          kind: "ACCEPTED",
          data: {
            ...receipt.outcome.data,
            operationId: prepared.input.operationId,
            scope: {
              ...receipt.outcome.data.subject,
              act: prepared.input.kind === "AUTHORITY" ? prepared.input.act.kind : "REGISTER",
            },
          },
        },
      },
    });
  });
  render(<AuthorityPanel token="token" onLockChange={vi.fn()} />);
  fireEvent.click((await screen.findAllByRole("button", { name: "View grants" }))[0]!);
  await waitFor(() => {
    expect(request).toHaveBeenCalledTimes(1);
  });
  await prepare();
  fireEvent.click(screen.getByRole("button", { name: "Record changes" }));
  await waitFor(() => {
    expect(service.authorityList).toHaveBeenCalledTimes(2);
  });
  expect(request.mock.calls[0]?.[3]?.aborted).toBe(true);
  await act(() =>
    Promise.resolve().then(() => {
      old.finish(grants(first));
    }),
  );
  expect(screen.queryByRole("heading", { name: "Actor grants" })).toBeNull();
  expect(screen.queryByText(`GRANT-${first}`)).toBeNull();
  expect(request).toHaveBeenCalledTimes(1);
});
it("clears pending pagination on back and never restores it on late completion", async () => {
  reads();
  const late = pending<Awaited<ReturnType<typeof service.authorityGrants>>>();
  const request = vi
    .spyOn(service, "authorityGrants")
    .mockResolvedValueOnce(grants(first, "next"))
    .mockReturnValueOnce(late.promise);
  render(<AuthorityPanel token="token" onLockChange={vi.fn()} />);
  fireEvent.click((await screen.findAllByRole("button", { name: "View grants" }))[0]!);
  fireEvent.click(await screen.findByRole("button", { name: "More grants" }));
  fireEvent.click(screen.getByRole("button", { name: "Back to actors" }));
  expect(request.mock.calls[1]?.[3]?.aborted).toBe(true);
  await act(() =>
    Promise.resolve().then(() => {
      late.finish(grants(first));
    }),
  );
  expect(screen.queryByRole("heading", { name: "Actor grants" })).toBeNull();
  expect(screen.queryByText(`GRANT-${first}`)).toBeNull();
});

it("resets only the selected grant page on invalid continuation and reloads it without a cursor", async () => {
  const request = vi
    .spyOn(service, "authorityGrants")
    .mockResolvedValueOnce(grants(first, "old"))
    .mockResolvedValueOnce({
      kind: "outcome",
      status: 200,
      value: {
        outcome: {
          kind: "REJECTED",
          data: {
            code: "INVALID_CURSOR",
            action: "READ_CURRENT",
            target: "cursor",
          },
        },
      },
    })
    .mockResolvedValueOnce(grants(first));
  const onRefused = vi.fn();
  render(
    <AuthorityGrants
      actorId={first}
      token="token"
      onBack={vi.fn()}
      onDisclosureRefused={onRefused}
    />,
  );
  fireEvent.click(await screen.findByRole("button", { name: "More grants" }));
  await screen.findByRole("alert");
  expect(screen.queryByText(`GRANT-${first}`)).toBeNull();
  expect(screen.queryByText(`header-${first}`)).toBeNull();
  expect(screen.queryByRole("button", { name: "More grants" })).toBeNull();
  expect(onRefused).not.toHaveBeenCalled();
  expect(request).toHaveBeenCalledTimes(2);
  fireEvent.click(screen.getByRole("button", { name: "Reload grants" }));
  await screen.findByText(`GRANT-${first}`);
  expect(request.mock.calls[2]?.[1]).toBeNull();
  expect(onRefused).not.toHaveBeenCalled();
});

const actorPage = (
  ids: string[],
  nextCursor: string | null = null,
): ApiResult<ServiceResponse<"authority.list">> => ({
  kind: "outcome",
  status: 200,
  value: {
    outcome: {
      kind: "SUCCEEDED",
      data: { items: ids.map((id) => actor(id, "listed")), nextCursor },
    },
  },
});
const focusedBack = () => {
  const back = screen.getByRole("button", { name: "Back to actors" });
  back.focus();
  expect(back).toHaveFocus();
  fireEvent.click(back);
};
it("returns focus to the originating grant control when it is still present", async () => {
  reads();
  vi.spyOn(service, "authorityGrants").mockResolvedValue(grants(first));
  render(<AuthorityPanel token="token" onLockChange={vi.fn()} />);
  const origin = (await screen.findAllByRole("button", { name: "View grants" }))[0]!;
  fireEvent.click(origin);
  await screen.findByText(`GRANT-${first}`);
  focusedBack();
  expect(origin).toHaveFocus();
});
it("focuses the stable actors heading during a pending reload and keeps it after the late page", async () => {
  reads();
  vi.spyOn(service, "authorityGrants").mockResolvedValue(grants(first));
  const late = pending<Awaited<ReturnType<typeof service.authorityList>>>();
  render(<AuthorityPanel token="token" onLockChange={vi.fn()} />);
  fireEvent.click((await screen.findAllByRole("button", { name: "View grants" }))[0]!);
  await screen.findByText(`GRANT-${first}`);
  vi.mocked(service.authorityList).mockReturnValueOnce(late.promise);
  fireEvent.click(screen.getByRole("button", { name: "Reload actors" }));
  expect(screen.queryByRole("button", { name: "View grants" })).toBeNull();
  focusedBack();
  const heading = screen.getByRole("heading", { name: "Actors and grants" });
  expect(heading).toHaveFocus();
  await act(() =>
    Promise.resolve().then(() => {
      late.finish(actorPage([first]));
    }),
  );
  await screen.findByRole("button", { name: "View grants" });
  expect(heading).toHaveFocus();
});
it("focuses the actors heading when a page-two origin is absent from refreshed page one", async () => {
  reads();
  vi.mocked(service.authorityList)
    .mockResolvedValueOnce(actorPage([first], "second"))
    .mockResolvedValueOnce(actorPage([second]))
    .mockResolvedValueOnce(actorPage([first]));
  vi.spyOn(service, "authorityGrants").mockResolvedValue(grants(second));
  render(<AuthorityPanel token="token" onLockChange={vi.fn()} />);
  fireEvent.click(await screen.findByRole("button", { name: "More actors" }));
  await screen.findByText(`listed-${second}`);
  fireEvent.click(screen.getAllByRole("button", { name: "View grants" })[1]!);
  await screen.findByText(`GRANT-${second}`);
  fireEvent.click(screen.getByRole("button", { name: "Reload actors" }));
  await screen.findByText(`listed-${first}`);
  expect(screen.queryByText(`listed-${second}`)).toBeNull();
  focusedBack();
  expect(screen.getByRole("heading", { name: "Actors and grants" })).toHaveFocus();
});
it("does not restore actor focus when global disclosure refusal removes the selected pane", async () => {
  reads();
  vi.spyOn(service, "authorityGrants")
    .mockResolvedValueOnce(grants(first, "next"))
    .mockResolvedValueOnce(refused("session"));
  render(<AuthorityPanel token="token" onLockChange={vi.fn()} />);
  fireEvent.click((await screen.findAllByRole("button", { name: "View grants" }))[0]!);
  await screen.findByText(`GRANT-${first}`);
  screen.getByRole("button", { name: "Back to actors" }).focus();
  const focus = vi.spyOn(HTMLElement.prototype, "focus");
  fireEvent.click(screen.getByRole("button", { name: "More grants" }));
  await waitFor(() => {
    expect(service.authorityList).toHaveBeenCalledTimes(2);
  });
  await screen.findByText(`listed-${first}`);
  expect(screen.queryByRole("heading", { name: "Actor grants" })).toBeNull();
  expect(focus).not.toHaveBeenCalled();
  expect(document.body).toHaveFocus();
});
