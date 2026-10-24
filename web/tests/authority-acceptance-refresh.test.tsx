import { authorityAcceptance, authorityPreview } from "./authority-operation.fixtures";
import { installationOffers } from "./installation-advice.fixtures";
import { act, fireEvent, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { AuthorityPanel } from "../src/views/AuthorityPanel";
import { service, type ApiResult, type ServiceResponse } from "../src/api/service";
import { wireReply } from "./contract-corpus.fixtures";

beforeEach(() => {
  authorityPreview();
});
afterEach(() => vi.restoreAllMocks());
const pending = <T,>() => {
  let finish: (value: T) => void = () => undefined;
  const promise = new Promise<T>((resolve) => {
    finish = resolve;
  });
  return { promise, finish };
};
const context = (): ApiResult<ServiceResponse<"context">> => {
  const value = wireReply("context");
  if (value.outcome.tag !== "SUCCEEDED") {
    throw new Error("Missing context fixture");
  }
  return {
    kind: "outcome",
    status: 200,
    value: {
      ...value,
      outcome: {
        ...value.outcome,
        data: {
          ...value.outcome.data,
          offers: installationOffers(["REGISTER_ACTOR", "SET_GRANT", "SET_ENABLED"]),
        },
      },
    },
  };
};
const listing = (): ApiResult<ServiceResponse<"authority.list">> => ({
  kind: "outcome",
  status: 200,
  value: wireReply("authority.list"),
});
const acceptance = (
  prepared: Parameters<typeof service.execute>[0],
): ApiResult<ServiceResponse<"operation.execute">> => authorityAcceptance(prepared);
const author = async (subject: string) => {
  await waitFor(() => {
    expect(document.querySelector('[name="target.subject"]')).not.toBeNull();
  });
  fireEvent.change(document.querySelector('[name="target.kind"]')!, {
    target: { value: "SERVICE" },
  });
  for (const [name, value] of [
    ["target.issuer", "https://issuer.example/"],
    ["target.subject", subject],
  ]) {
    fireEvent.change(document.querySelector(`[name="${name}"]`)!, { target: { value } });
  }
};
const finish = async <T,>(read: ReturnType<typeof pending<T>>, value: T) => {
  await act(async () => {
    read.finish(value);
    await read.promise;
  });
};
const selectEnabled = async () => {
  await waitFor(() => {
    expect(
      screen.getByLabelText("Authority action", { exact: true }).querySelectorAll("option"),
    ).toHaveLength(3);
  });
  fireEvent.change(screen.getByLabelText("Authority action", { exact: true }), {
    target: { value: "SET_ENABLED" },
  });
};
it.each(["context", "listing"] as const)(
  "accepted enablement preserves the next authored request across %s-first disclosure refresh",
  async (first) => {
    const contextRead = pending<Awaited<ReturnType<typeof service.context>>>();
    const listingRead = pending<Awaited<ReturnType<typeof service.authorityList>>>();
    const execution = pending<Awaited<ReturnType<typeof service.execute>>>();
    vi.spyOn(service, "context")
      .mockResolvedValueOnce(context())
      .mockReturnValue(contextRead.promise);
    vi.spyOn(service, "authorityList")
      .mockResolvedValueOnce(listing())
      .mockReturnValue(listingRead.promise);
    const execute = vi.spyOn(service, "execute").mockReturnValue(execution.promise);
    render(<AuthorityPanel token="synthetic" onLockChange={vi.fn()} />);
    await selectEnabled();
    await author("synthetic-first");
    fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
    fireEvent.click(await screen.findByRole("checkbox", { name: "I confirm this exact request." }));
    fireEvent.click(screen.getByRole("button", { name: "Record changes" }));
    const [prepared] = execute.mock.calls[0]!;
    expect(prepared.input.act).toEqual({ kind: "SET_ENABLED", enabled: false });
    await finish(execution, acceptance(prepared));
    expect(screen.queryByRole("button", { name: "Record changes" })).toBeNull();
    if (first === "listing") {
      await finish(listingRead, listing());
      expect(screen.queryByLabelText("Authority action", { exact: true })).toBeNull();
    }
    await finish(contextRead, context());
    fireEvent.click(screen.getByRole("button", { name: "Start another operation" }));
    fireEvent.change(screen.getByLabelText("Authority action", { exact: true }), {
      target: { value: "SET_GRANT" },
    });
    await author("synthetic-next");
    if (first === "context") {
      await finish(listingRead, listing());
    }
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByLabelText("Authority action", { exact: true })).toHaveValue("SET_GRANT");
    expect(document.querySelector('[name="target.subject"]')).toHaveValue("synthetic-next");
    fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
    expect(await screen.findByRole("heading", { name: "Review changes" })).toBeVisible();
    expect(execute).toHaveBeenCalledOnce();
  },
);

const failedContext = (
  kind: "transport" | "host" | "core",
): ApiResult<ServiceResponse<"context">> => {
  if (kind === "transport") {
    return { kind: "deliveryFailure", notice: { kind: "local", reason: "unreachable" } };
  }
  if (kind === "host") {
    return {
      kind: "hostFailure",
      status: 400,
      failure: { code: "INPUT_INVALID", phase: "NOT_STARTED" },
    };
  }
  return {
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        tag: "FAILED",
        data: { code: "CORE_STORE_UNAVAILABLE", action: "RETRY_SAFE" },
      },
    },
  };
};
it.each(["transport", "host", "core"] as const)(
  "accepted operation surfaces %s context refresh failure and allows explicit fresh reload",
  async (kind) => {
    const reads = vi
      .spyOn(service, "context")
      .mockResolvedValueOnce(context())
      .mockResolvedValueOnce(failedContext(kind))
      .mockResolvedValue(context());
    vi.spyOn(service, "authorityList").mockResolvedValue(listing());
    vi.spyOn(service, "execute").mockImplementation((prepared) =>
      Promise.resolve(acceptance(prepared)),
    );
    render(<AuthorityPanel token="synthetic" onLockChange={vi.fn()} />);
    await author("synthetic-first");
    fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
    fireEvent.click(await screen.findByRole("checkbox", { name: "I confirm this exact request." }));
    fireEvent.click(screen.getByRole("button", { name: "Record changes" }));
    expect(await screen.findByRole("alert")).toBeVisible();
    expect(screen.queryByLabelText("Authority action", { exact: true })).toBeNull();
    expect(reads).toHaveBeenCalledTimes(2);
    fireEvent.click(screen.getByRole("button", { name: /^Try again$/u }));
    fireEvent.click(screen.getByRole("button", { name: "Start another operation" }));
    await waitFor(() => {
      expect(
        screen.getByLabelText("Authority action", { exact: true }).querySelectorAll("option"),
      ).toHaveLength(3);
    });
    expect(screen.queryByRole("alert")).toBeNull();
    expect(reads).toHaveBeenCalledTimes(3);
  },
);
it("context reload preserves an uncertain prepared authority request and exact retry identity", async () => {
  const oldListing = pending<Awaited<ReturnType<typeof service.authorityList>>>();
  vi.spyOn(service, "context")
    .mockResolvedValueOnce(context())
    .mockResolvedValueOnce(failedContext("transport"))
    .mockResolvedValue(context());
  vi.spyOn(service, "authorityList")
    .mockReturnValueOnce(oldListing.promise)
    .mockResolvedValue(listing());
  const execute = vi
    .spyOn(service, "execute")
    .mockResolvedValueOnce({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    })
    .mockImplementation((prepared) => Promise.resolve(acceptance(prepared)));
  render(<AuthorityPanel token="synthetic" onLockChange={vi.fn()} />);
  await author("synthetic-pending");
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  fireEvent.click(await screen.findByRole("checkbox", { name: "I confirm this exact request." }));
  fireEvent.click(screen.getByRole("button", { name: "Record changes" }));
  await screen.findByRole("button", { name: "Retry the same operation" });
  const [prepared] = execute.mock.calls[0]!;
  await finish(oldListing, {
    kind: "hostFailure",
    status: 403,
    failure: { code: "SESSION_FORBIDDEN", phase: "NOT_STARTED" },
  });
  fireEvent.click(await screen.findByRole("button", { name: /^Try again$/u }));
  await waitFor(() => {
    expect(screen.queryByRole("button", { name: /^Try again$/u })).toBeNull();
  });
  expect(execute.mock.calls[0]![0]).toBe(prepared);
  expect(screen.queryByText("synthetic-pending")).toBeNull();
  expect(screen.queryByRole("button", { name: "Review changes" })).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Retry the same operation" }));
  await waitFor(() => {
    expect(screen.queryByRole("button", { name: "Retry the same operation" })).toBeNull();
  });
  expect(execute.mock.calls[1]![0]).toBe(prepared);
  expect(execute.mock.calls[1]![0].bytes).toBe(prepared.bytes);
});
