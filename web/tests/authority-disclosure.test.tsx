import { authorityPreview } from "./authority-operation.fixtures";
import { act, fireEvent, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { Dashboard } from "../src/views/Dashboard";
import { AuthorityPanel } from "../src/views/AuthorityPanel";
import { service, type ApiResult, type ServiceResponse } from "../src/api/service";
import { wireReply } from "./contract-corpus.fixtures";

beforeEach(() => {
  authorityPreview();
});
afterEach(() => vi.restoreAllMocks());
const disclosedActor = {
  actorId: "10000000-0000-4000-8000-000000000001",
  principal: {
    kind: "SERVICE",
    issuer: "https://issuer.example/",
    subject: "synthetic-disclosed",
  },
  enabled: true,
} as const;
const success = <K extends "context" | "authority.list">(
  endpoint: K,
): ApiResult<ServiceResponse<K>> => {
  const value = wireReply(endpoint);
  if (value.outcome.tag !== "SUCCEEDED") {
    throw new Error("Missing successful fixture");
  }
  if (endpoint === "context") {
    return {
      kind: "outcome",
      status: 200,
      value: {
        ...value,
        outcome: {
          ...value.outcome,
          data: {
            ...value.outcome.data,
            views: ["CASES", "AUTHORITY", "OPERATIONS"],
            offers: [
              {
                kind: "AUTHORITY_ACT",
                act: "REGISTER",
              },
            ],
          },
        },
      },
    };
  }
  return {
    kind: "outcome",
    status: 200,
    value: {
      ...value,
      outcome: {
        ...value.outcome,
        data: {
          items: [disclosedActor],
          nextCursor: null,
        },
      },
    },
  };
};
const refused = (): Extract<
  ApiResult<ServiceResponse<"operation.execute">>,
  { kind: "hostFailure" }
> => ({
  kind: "hostFailure",
  status: 403,
  failure: { code: "SESSION_FORBIDDEN", phase: "NOT_STARTED" },
});
const finishRead = (complete: () => void) => act(() => Promise.resolve().then(complete));
const unavailable = (): ApiResult<ServiceResponse<"operation.execute">> => ({
  kind: "outcome",
  status: 200,
  value: {
    operationId: "10000000-0000-4000-8000-000000000001",
    outcome: {
      tag: "REJECTED",
      data: {
        code: "ACCESS_RESOURCE_UNAVAILABLE",
        action: "NONE_REQUIRED",
        target: null,
        actualRevision: null,
        limits: {},
      },
    },
  },
});
const author = async () => {
  await waitFor(() => {
    expect(document.querySelector('[name="target.subject"]')).not.toBeNull();
  });
  for (const [name, value] of [
    ["target.issuer", "https://issuer.example/"],
    ["target.subject", "synthetic-authored"],
  ]) {
    fireEvent.change(document.querySelector(`[name="${name}"]`)!, { target: { value } });
  }
};
const prepare = async () => {
  await author();
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  fireEvent.click(await screen.findByRole("checkbox"));
};
const listPrincipal = () => "synthetic-disclosed";
const emptyOffers = () => {
  const response = wireReply("context");
  if (response.outcome.tag !== "SUCCEEDED") {
    throw new Error("Missing context fixture");
  }
  return {
    kind: "outcome" as const,
    status: 200,
    value: {
      ...response,
      outcome: { ...response.outcome, data: { ...response.outcome.data, offers: [] } },
    },
  };
};
it.each([false, true])(
  "revalidates authority disclosure after management refusal; retained read=%s",
  async (retainsRead) => {
    const context = vi
      .spyOn(service, "context")
      .mockResolvedValueOnce(success("context"))
      .mockResolvedValue(emptyOffers());
    const listing = vi
      .spyOn(service, "authorityList")
      .mockResolvedValueOnce(success("authority.list"));
    let finish: ((value: Awaited<ReturnType<typeof service.authorityList>>) => void) | undefined;
    listing.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    vi.spyOn(service, "execute").mockResolvedValue(unavailable());
    render(<AuthorityPanel token="synthetic-token" onLockChange={vi.fn()} />);
    await screen.findByText(listPrincipal());
    await prepare();
    fireEvent.click(screen.getByRole("button", { name: "Record changes" }));
    await waitFor(() => {
      expect(context).toHaveBeenCalledTimes(2);
    });
    expect(screen.queryByText(listPrincipal())).toBeNull();
    expect(screen.queryByText("synthetic-authored")).toBeNull();
    await finishRead(() => {
      finish?.(retainsRead ? success("authority.list") : refused());
    });
    if (retainsRead) {
      await screen.findByText(listPrincipal());
    } else {
      expect(screen.queryByText(listPrincipal())).toBeNull();
    }
    expect(screen.queryByRole("option")).toBeNull();
    expect(listing).toHaveBeenCalledTimes(2);
  },
);
it("aborts prior authority reads and ignores their late completion after an uncertain dispatch refusal", async () => {
  vi.spyOn(service, "context")
    .mockResolvedValueOnce(success("context"))
    .mockResolvedValue(emptyOffers());
  let priorFinish: ((value: Awaited<ReturnType<typeof service.authorityList>>) => void) | undefined;
  const listing = vi
    .spyOn(service, "authorityList")
    .mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          priorFinish = resolve;
        }),
    )
    .mockResolvedValue(refused());
  const execute = vi
    .spyOn(service, "execute")
    .mockResolvedValueOnce({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    })
    .mockResolvedValue(unavailable());
  render(<AuthorityPanel token="synthetic-token" onLockChange={vi.fn()} />);
  await prepare();
  fireEvent.click(screen.getByRole("button", { name: "Record changes" }));
  fireEvent.click(await screen.findByRole("button", { name: "Send again" }));
  await waitFor(() => {
    expect(listing).toHaveBeenCalledTimes(2);
  });
  expect(listing.mock.calls[0]![2]!.aborted).toBe(true);
  await finishRead(() => {
    priorFinish?.(success("authority.list"));
  });
  expect(screen.queryByText(listPrincipal())).toBeNull();
  expect(screen.getByRole("button", { name: "Send again" })).toBeVisible();
  expect(execute.mock.calls[1]![0]).toBe(execute.mock.calls[0]![0]);
  expect(execute.mock.calls[1]![0].bytes).toBe(execute.mock.calls[0]![0].bytes);
});
const revalidatingReads = async () => {
  let finishContext: ((value: Awaited<ReturnType<typeof service.context>>) => void) | undefined;
  let finishListing:
    ((value: Awaited<ReturnType<typeof service.authorityList>>) => void) | undefined;
  const context = vi
    .spyOn(service, "context")
    .mockResolvedValueOnce(success("context"))
    .mockImplementation(
      () =>
        new Promise((resolve) => {
          finishContext = resolve;
        }),
    );
  const listing = vi
    .spyOn(service, "authorityList")
    .mockResolvedValueOnce(success("authority.list"))
    .mockImplementation(
      () =>
        new Promise((resolve) => {
          finishListing = resolve;
        }),
    );
  vi.spyOn(service, "execute").mockResolvedValue(unavailable());
  render(<AuthorityPanel token="synthetic-token" onLockChange={vi.fn()} />);
  await screen.findByText(listPrincipal());
  await prepare();
  fireEvent.click(screen.getByRole("button", { name: "Record changes" }));
  await waitFor(() => {
    expect(listing).toHaveBeenCalledTimes(2);
  });
  return { context, listing, finishContext, finishListing };
};
it("keeps discard actionable when readonly restoration completes during a pointer press", async () => {
  const { finishContext, finishListing } = await revalidatingReads();
  const user = userEvent.setup();
  const discard = screen.getByRole("button", { name: "Discard and leave" });
  expect(discard).not.toBeDisabled();
  await user.pointer({ target: discard, keys: "[MouseLeft>]" });
  await finishRead(() => {
    finishContext?.(emptyOffers());
    finishListing?.(success("authority.list"));
  });
  expect(screen.getByRole("button", { name: "Discard and leave" })).toBe(discard);
  await user.pointer({ keys: "[/MouseLeft]" });
  const dialog = screen.getByRole("dialog");
  expect(dialog).toBeVisible();
  await user.click(within(dialog).getByRole("button", { name: "Discard and leave" }));
  expect(screen.queryByRole("dialog")).toBeNull();
});
it.each(["context", "listing"] as const)(
  "a %s success during revalidation cannot survive the other read's refusal",
  async (first) => {
    const { context, listing, finishContext, finishListing } = await revalidatingReads();
    await finishRead(() => {
      if (first === "context") {
        finishContext?.(success("context"));
      } else {
        finishListing?.(success("authority.list"));
      }
    });
    if (first === "listing") {
      expect(screen.getByText(listPrincipal())).toBeVisible();
    }
    await finishRead(() => {
      if (first === "context") {
        finishListing?.(refused());
      } else {
        finishContext?.(refused());
      }
    });
    expect(screen.queryByText(listPrincipal())).toBeNull();
    expect(context).toHaveBeenCalledTimes(2);
    expect(listing).toHaveBeenCalledTimes(2);
    expect(screen.queryByText("synthetic-authored")).toBeNull();
    expect(screen.queryByRole("option")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Reload actors" }));
    await waitFor(() => {
      expect(listing).toHaveBeenCalledTimes(3);
    });
    expect(context).toHaveBeenCalledTimes(3);
  },
);
it.each([
  { stage: "unprepared", reviewed: false, returned: false },
  { stage: "reviewed", reviewed: true, returned: false },
  { stage: "returned to editing", reviewed: true, returned: true },
])(
  "explicitly discards a $stage draft after management loss and restores dashboard navigation",
  async ({ reviewed, returned }) => {
    const user = userEvent.setup();
    const logout = vi.fn().mockResolvedValue(undefined);
    const context = vi.spyOn(service, "context").mockResolvedValue(success("context"));
    const listing = vi.spyOn(service, "authorityList").mockResolvedValue(success("authority.list"));
    vi.spyOn(service, "list").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: wireReply("case.list"),
    });
    vi.spyOn(service, "execute").mockImplementation(() => {
      context.mockResolvedValue(emptyOffers());
      return Promise.resolve(unavailable());
    });
    render(<Dashboard token="synthetic-token" onLogout={logout} />);
    await user.click(await screen.findByRole("button", { name: "Authority" }));
    if (reviewed) {
      await prepare();
      if (returned) {
        await user.click(screen.getByRole("button", { name: "Return to editing" }));
        context.mockResolvedValue(emptyOffers());
        listing.mockResolvedValue(refused());
        await user.click(screen.getByRole("button", { name: "Reload actors" }));
      } else {
        await user.click(screen.getByRole("button", { name: "Record changes" }));
      }
    } else {
      await author();
      context.mockResolvedValue(emptyOffers());
      listing.mockResolvedValue(refused());
      await user.click(screen.getByRole("button", { name: "Reload actors" }));
    }
    await waitFor(() => {
      expect(context).toHaveBeenCalledTimes(3);
    });
    expect(screen.getByRole("button", { name: "Sign out" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cases" })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "Discard and leave" }));
    await user.click(
      within(screen.getByRole("dialog")).getByRole("button", { name: "Discard and leave" }),
    );
    expect(screen.getByRole("button", { name: "Sign out" })).not.toBeDisabled();
    expect(screen.getByRole("button", { name: "Cases" })).not.toBeDisabled();
    expect(screen.queryByText("synthetic-authored")).toBeNull();
    await user.click(screen.getByRole("button", { name: "Cases" }));
    await user.click(screen.getByRole("button", { name: "Sign out" }));
    expect(logout).toHaveBeenCalledOnce();
  },
);
const authorityReceipt = () => {
  const accepted = wireReply("operation.execute", "AUTHORITY");
  if (accepted.outcome.tag !== "ACCEPTED" || accepted.outcome.data.scope.kind !== "AUTHORITY") {
    throw new Error("Missing authority receipt fixture");
  }
  return { ...accepted.outcome.data, scope: accepted.outcome.data.scope };
};
const authorityRecoveryDelivery = (first: "lost" | "unknown") => {
  const receipt = authorityReceipt();
  return vi
    .spyOn(service, "execute")
    .mockImplementationOnce((prepared) =>
      Promise.resolve(
        first === "lost"
          ? {
              kind: "deliveryFailure",
              notice: { kind: "local", reason: "unreachable" },
            }
          : {
              kind: "outcome",
              status: 200,
              value: {
                operationId: prepared.input.operationId,
                outcome: {
                  tag: "OUTCOME_UNKNOWN",
                  data: null,
                },
              },
            },
      ),
    )
    .mockImplementation((prepared) =>
      Promise.resolve({
        kind: "outcome",
        status: 200,
        value: {
          operationId: prepared.input.operationId,
          outcome: {
            tag: "ALREADY_ACCEPTED",
            data: {
              ...receipt,
              operationId: prepared.input.operationId,
              scope: {
                ...receipt.scope,
                act: prepared.input.scope === "AUTHORITY" ? prepared.input.act.kind : "REGISTER",
              },
            },
          },
        },
      }),
    );
};
it.each(["lost", "unknown"] as const)(
  "removes the obsolete %s attempt alert after exact authority replay acceptance",
  async (first) => {
    vi.spyOn(service, "context").mockResolvedValue(success("context"));
    vi.spyOn(service, "authorityList").mockResolvedValue(success("authority.list"));
    const execute = authorityRecoveryDelivery(first);
    render(<AuthorityPanel token="synthetic-token" onLockChange={vi.fn()} />);
    await prepare();
    fireEvent.click(screen.getByRole("button", { name: "Record changes" }));
    const retry = await screen.findByRole("button", { name: "Send again" });
    expect(screen.getByRole("alert")).toBeVisible();
    const [pending] = execute.mock.calls[0]!;
    fireEvent.click(retry);
    await waitFor(() => {
      expect(screen.queryByRole("alert")).toBeNull();
    });
    expect(
      (await screen.findByText(pending.input.operationId, { exact: false })).closest(
        '[role="status"]',
      ),
    ).not.toBeNull();
    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "accepted" })).toHaveFocus();
    });
    expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
    expect(execute.mock.calls[1]![0]).toBe(pending);
    expect(execute.mock.calls[1]![0].bytes).toBe(pending.bytes);
  },
);
