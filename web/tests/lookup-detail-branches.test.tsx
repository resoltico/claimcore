import { caseOffers } from "./case-advice.fixtures";
import { act } from "@testing-library/react";
import { render, screen, waitFor } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { CaseDetail } from "../src/views/CaseDetail";
import { OperationLookup } from "../src/views/OperationLookup";
import { hostCases } from "./contract-corpus.fixtures";
import { CaseList } from "../src/views/CaseList";
import {
  unavailable,
  context,
  definition,
  fields,
  operationId,
  response,
} from "./service-ui.fixtures";

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("does not request history or acts for a missing case", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("case.get", "REJECTED", unavailable));
  render(
    <CaseDetail
      token="token"
      caseReference="MISSING"
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onCommand={vi.fn()}
    />,
  );
  expect(await screen.findByText("The requested resource is unavailable.")).toBeVisible();
  expect(screen.queryByRole("heading", { name: "Case history" })).toBeNull();
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(screen.queryByRole("heading", { name: "Available actions" })).toBeNull();
});

it("keeps a failed history page request-local without erasing current case data", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.get", "SUCCEEDED", {
      caseReference: fields.caseReference,
      revision: 1,
      advice: { acts: caseOffers([]), history: ["SUMMARY", "FULL"] },

      state: { tag: "LIVE", fields, disposition: "ACTIVE" },
    }),
  );
  fetch.mockResolvedValueOnce(
    new Response("invalid", { headers: { "content-type": "application/json" } }),
  );
  render(
    <CaseDetail
      token="token"
      caseReference="CASE-1"
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onCommand={vi.fn()}
    />,
  );
  expect(await screen.findByText("CASE-1")).toBeVisible();
  expect(await screen.findByRole("alert")).toBeVisible();
});

const readableCurrent = () =>
  response("case.get", "SUCCEEDED", {
    caseReference: fields.caseReference,
    revision: 1,
    advice: { acts: caseOffers([]), history: ["SUMMARY", "FULL"] },

    state: { tag: "LIVE", fields, disposition: "ACTIVE" },
  });

it("shows a typed unavailable history refusal after revalidating readable current fields", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  let release: ((value: Response) => void) | undefined;
  const revalidation = new Promise<Response>((resolve) => {
    release = resolve;
  });
  fetch.mockResolvedValueOnce(readableCurrent());
  fetch.mockResolvedValueOnce(response("case.history", "REJECTED", unavailable));
  fetch.mockReturnValueOnce(revalidation);
  render(
    <CaseDetail
      token="token"
      caseReference="CASE-1"
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onCommand={vi.fn()}
    />,
  );
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(3);
  });
  await waitFor(() => {
    expect(screen.queryByRole("region", { name: "current case" })).toBeNull();
  });
  await act(async () => {
    release?.(readableCurrent());
    await revalidation;
  });
  expect(await screen.findByRole("region", { name: "current case" })).toBeVisible();
  expect(screen.getByRole("heading", { name: "Case history" })).toBeVisible();
  expect(screen.getByRole("alert")).toHaveTextContent("The requested resource is unavailable.");
  expect(fetch.mock.calls[2]?.[0]).toBe("/api/case.get");
});

it("reports absent and malformed operation observations without leaving stale receipts", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("operation.observe", "REJECTED", unavailable));
  render(<OperationLookup token="token" definition={definition} />);
  expect(screen.getByRole("button", { name: "Look up recorded result" })).toBeDisabled();
  await user.type(screen.getByLabelText("Exact operation ID"), operationId);
  await user.click(screen.getByRole("button", { name: "Look up recorded result" }));
  expect(await screen.findByText("The requested resource is unavailable.")).toBeVisible();
  expect(screen.getByRole("alert")).toHaveTextContent("The requested resource is unavailable.");
  expect(screen.queryByText(/Accepted operation/u)).toBeNull();
});

it("reports a found observation without a receipt as a protocol failure", async () => {
  const user = userEvent.setup();
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    response("operation.observe", "SUCCEEDED", { tag: "FOUND" }),
  );
  render(<OperationLookup token="token" definition={definition} />);
  await user.type(screen.getByLabelText("Exact operation ID"), operationId);
  await user.click(screen.getByRole("button", { name: "Look up recorded result" }));
  expect(await screen.findByRole("alert")).toBeVisible();
});

it("focuses and describes invalid operation identity then clears the stale error [CC-WEB-001]", async () => {
  const sample = hostCases().find((value) => value.id === "input-invalid");
  if (sample === undefined) {
    throw new Error("UUID host diagnostic fixture is required.");
  }
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    new Response(JSON.stringify(sample.value), {
      status: sample.status,
      headers: { "content-type": "application/json" },
    }),
  );
  const user = userEvent.setup();
  render(<OperationLookup token="token" definition={definition} />);
  const input = screen.getByLabelText("Exact operation ID");
  await user.type(input, "invalid");
  await user.click(screen.getByRole("button", { name: "Look up recorded result" }));
  await waitFor(() => {
    expect(input).toHaveFocus();
  });
  expect(input).toHaveAttribute("aria-invalid", "true");
  expect(input).toHaveAttribute("aria-describedby");
  expect(document.getElementById(input.getAttribute("aria-describedby")!)).toHaveTextContent(
    "The input does not satisfy the endpoint contract.",
  );
  await user.type(input, "a");
  expect(input).not.toHaveAttribute("aria-invalid", "true");
  expect(screen.queryByRole("alert")).toBeNull();
});

it("requires an exact nonempty reference without silently trimming lookup [CC-WEB-001]", async () => {
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    response("case.list", "SUCCEEDED", { offers: context.offers, items: [], nextCursor: null }),
  );
  const selected = vi.fn();
  const user = userEvent.setup();
  render(<CaseList token="token" onSelect={selected} onOpen={vi.fn()} />);
  const find = screen.getByRole("button", { name: "Find case" });
  expect(find).toBeDisabled();
  await user.type(screen.getByLabelText("Handler's case reference (exact)"), " REF ");
  await user.click(find);
  expect(selected).toHaveBeenCalledWith(" REF ");
  expect(globalThis.fetch).toHaveBeenCalledTimes(1);
});
