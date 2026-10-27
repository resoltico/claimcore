import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import { CaseHistory } from "../../../src/views/CaseHistory";
import { definition, response } from "../../support/service-ui.fixtures";
import { historyVariants } from "../../support/contract-corpus.fixtures";

const expectHistoryRequest = (body: unknown) => {
  if (typeof body !== "string") {
    throw new Error("Synthetic history input must be JSON text.");
  }
  expect(JSON.parse(body)).toMatchObject({ detail: "SUMMARY", caseReference: "CASE-1", limit: 50 });
};
it("defaults to actual SUMMARY entries and deliberately resets between SUMMARY and FULL", async () => {
  const { summary, full } = historyVariants();
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((_url, init) => {
    if (typeof init?.body !== "string") {
      throw new Error("Synthetic history input must be JSON text.");
    }
    const input: unknown = JSON.parse(init.body);
    if (typeof input !== "object" || input === null || !("detail" in input)) {
      throw new Error("History mode is missing.");
    }
    return Promise.resolve(
      response("case.history", "SUCCEEDED", {
        entries: [input.detail === "FULL" ? full : summary],
        nextCursor: null,
      }),
    );
  });
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  const view = render(
    <CaseHistory
      token="token"
      caseReference="CASE-1"
      fields={definition.catalog.fields}
      modes={["SUMMARY", "FULL"]}
    />,
  );
  await waitFor(() => {
    expect(view.container.querySelectorAll(".history-list li")).toHaveLength(1);
  });
  expectHistoryRequest(fetch.mock.calls[0]?.[1]?.body);
  expect(view.container.querySelector(".history-list details")).toBeNull();
  expect(view.container.querySelector(".case-fields")).toBeNull();
  expect(screen.getByRole("combobox", { name: "History detail" })).toBeVisible();
  await user.selectOptions(screen.getByLabelText("History detail"), "FULL");
  await waitFor(() => {
    expect(view.container.querySelector(".history-list details")).not.toBeNull();
  });
  await user.selectOptions(screen.getByLabelText("History detail"), "SUMMARY");
  await waitFor(() => {
    expect(view.container.querySelector(".history-list details")).toBeNull();
  });
  expect(view.container.querySelectorAll(".history-list li")).toHaveLength(1);
  expect(fetch).toHaveBeenCalledTimes(3);
});

it("recovers a refused continuation with a fresh no-cursor request without reporting lost disclosure", async () => {
  const { summary } = historyVariants();
  const refused = vi.fn();
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(
      response("case.history", "SUCCEEDED", { entries: [summary], nextCursor: "old" }),
    )
    .mockResolvedValueOnce(
      response("case.history", "REJECTED", {
        code: "INVALID_CURSOR",
        action: "READ_CURRENT",
        target: "cursor",
      }),
    )
    .mockResolvedValueOnce(
      response("case.history", "SUCCEEDED", { entries: [summary], nextCursor: null }),
    );
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  const view = render(
    <CaseHistory
      token="token"
      caseReference="CASE-1"
      fields={definition.catalog.fields}
      onDisclosureRefused={refused}
    />,
  );
  await user.click(await screen.findByRole("button", { name: "Load more history" }));
  await screen.findByRole("alert");
  expect(view.container.querySelectorAll(".history-list li")).toHaveLength(0);
  expect(screen.queryByRole("button", { name: "Load more history" })).toBeNull();
  expect(refused).not.toHaveBeenCalled();
  expect(fetch).toHaveBeenCalledTimes(2);
  await user.click(screen.getByRole("button", { name: "Reload history" }));
  await waitFor(() => {
    expect(view.container.querySelectorAll(".history-list li")).toHaveLength(1);
  });
  const body = fetch.mock.calls[2]?.[1]?.body;
  if (typeof body !== "string") {
    throw new Error("Expected history request bytes.");
  }
  expect(JSON.parse(body)).not.toHaveProperty("cursor");
  expect(refused).not.toHaveBeenCalled();
});
