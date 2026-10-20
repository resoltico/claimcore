import { installationOffers } from "./installation-advice.fixtures";
import { act, fireEvent, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { AuthorityPanel } from "../src/views/AuthorityPanel";
import { service, type ApiResult, type ServiceResponse } from "../src/api/service";
import { generatedWebValue } from "./contract-corpus.fixtures";

afterEach(() => vi.restoreAllMocks());
const pending = <T,>() => {
  let finish: (value: T) => void = () => undefined;
  const promise = new Promise<T>((resolve) => {
    finish = resolve;
  });
  return { promise, finish };
};
const context = (): ApiResult<ServiceResponse<"context">> => {
  const value = generatedWebValue("context");
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
  value: generatedWebValue("authority.list"),
});
const acceptance = (
  prepared: Parameters<typeof service.authorityExecute>[0],
): ApiResult<ServiceResponse<"authority.execute">> => {
  const value = generatedWebValue("authority.execute");
  if (value.outcome.tag !== "ACCEPTED") {
    throw new Error("Missing acceptance fixture");
  }
  return {
    kind: "outcome",
    status: 200,
    value: {
      ...value,
      operationId: prepared.input.operationId,
      outcome: {
        tag: "ACCEPTED",
        data: {
          ...value.outcome.data,
          operationId: prepared.input.operationId,
          act: prepared.input.act.kind,
        },
      },
    },
  };
};
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
    const execution = pending<Awaited<ReturnType<typeof service.authorityExecute>>>();
    vi.spyOn(service, "context")
      .mockResolvedValueOnce(context())
      .mockReturnValue(contextRead.promise);
    vi.spyOn(service, "authorityList")
      .mockResolvedValueOnce(listing())
      .mockReturnValue(listingRead.promise);
    const execute = vi.spyOn(service, "authorityExecute").mockReturnValue(execution.promise);
    render(<AuthorityPanel token="synthetic" onLockChange={vi.fn()} />);
    await selectEnabled();
    await author("synthetic-first");
    fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "I confirm this exact request." }));
    fireEvent.click(screen.getByRole("button", { name: "Submit operation" }));
    const [prepared] = execute.mock.calls[0]!;
    expect(prepared.input.act).toEqual({ kind: "SET_ENABLED", enabled: false });
    await finish(execution, acceptance(prepared));
    expect(screen.queryByRole("button", { name: "Submit operation" })).toBeNull();
    if (first === "listing") {
      await finish(listingRead, listing());
      expect(
        screen.getByLabelText("Authority action", { exact: true }).querySelectorAll("option"),
      ).toHaveLength(0);
      expect(screen.getByLabelText("Authority action", { exact: true })).toBeDisabled();
    }
    await finish(contextRead, context());
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
    expect(screen.getByRole("heading", { name: "Review exact request" })).toBeVisible();
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
      failure: {
        kind: "HOST_FAILURE",
        code: "INPUT_INVALID",
        status: 400,
        diagnostic: { id: "HOST_INPUTINVALID", parameters: {} },
        message: "Synthetic host refusal",
        executionPhase: "NOT_STARTED",
      },
    };
  }
  return {
    kind: "outcome",
    status: 200,
    value: {
      endpoint: "context",
      outcome: {
        tag: "FAILED",
        data: {
          diagnostic: { id: "CORE_STORE_UNAVAILABLE", parameters: {} },
          message: "Synthetic unavailable store",
          recommendedAction: "RETRY_SAFE",
        },
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
    vi.spyOn(service, "authorityExecute").mockImplementation((prepared) =>
      Promise.resolve(acceptance(prepared)),
    );
    render(<AuthorityPanel token="synthetic" onLockChange={vi.fn()} />);
    await author("synthetic-first");
    fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
    fireEvent.click(screen.getByRole("checkbox", { name: "I confirm this exact request." }));
    fireEvent.click(screen.getByRole("button", { name: "Submit operation" }));
    expect(await screen.findByRole("alert")).toBeVisible();
    expect(
      screen.getByLabelText("Authority action", { exact: true }).querySelectorAll("option"),
    ).toHaveLength(0);
    expect(screen.getByLabelText("Authority action", { exact: true })).toBeDisabled();
    expect(reads).toHaveBeenCalledTimes(2);
    fireEvent.click(screen.getByRole("button", { name: /^Try again$/u }));
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
    .spyOn(service, "authorityExecute")
    .mockResolvedValueOnce({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    })
    .mockImplementation((prepared) => Promise.resolve(acceptance(prepared)));
  render(<AuthorityPanel token="synthetic" onLockChange={vi.fn()} />);
  await author("synthetic-pending");
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  fireEvent.click(screen.getByRole("checkbox", { name: "I confirm this exact request." }));
  fireEvent.click(screen.getByRole("button", { name: "Submit operation" }));
  await screen.findByRole("button", { name: "Retry the same operation" });
  const [prepared] = execute.mock.calls[0]!;
  await finish(oldListing, {
    kind: "hostFailure",
    status: 403,
    failure: {
      kind: "HOST_FAILURE",
      code: "SESSION_FORBIDDEN",
      status: 403,
      diagnostic: { id: "HOST_SESSIONFORBIDDEN", parameters: {} },
      message: "Synthetic forbidden disclosure",
      executionPhase: "NOT_STARTED",
    },
  });
  fireEvent.click(await screen.findByRole("button", { name: /^Try again$/u }));
  await waitFor(() => {
    expect(screen.queryByRole("button", { name: /^Try again$/u })).toBeNull();
  });
  expect(screen.getByText("synthetic-pending")).toBeVisible();
  expect(screen.queryByRole("button", { name: "Review changes" })).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Retry the same operation" }));
  await waitFor(() => {
    expect(screen.queryByRole("button", { name: "Retry the same operation" })).toBeNull();
  });
  expect(execute.mock.calls[1]![0]).toBe(prepared);
  expect(execute.mock.calls[1]![0].bytes).toBe(prepared.bytes);
});
