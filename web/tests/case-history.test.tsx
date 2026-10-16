import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "./presentation-test-support";
import { CaseHistory } from "../src/views/CaseHistory";
import { definition, response } from "./service-ui.fixtures";
import { generatedWebValue } from "./contract-corpus.fixtures";

const historyVariants = () => {
  const source = generatedWebValue("case.history").outcome;
  if (source.tag !== "SUCCEEDED") {
    throw new Error("Synthetic history is missing.");
  }
  const summary = source.data.entries.find((entry) => entry.tag === "SUMMARY");
  const full = source.data.entries.find((entry) => entry.tag === "FULL");
  if (summary === undefined || full === undefined) {
    throw new Error("Synthetic variants are missing.");
  }
  return { summary, full };
};

const expectHistoryRequest = (body: unknown) => {
  if (typeof body !== "string") {
    throw new Error("Synthetic history input must be JSON text.");
  }
  expect(JSON.parse(body)).toMatchObject({ detail: "SUMMARY", caseReference: "CASE-1", limit: 50 });
};
it("defaults to actual SUMMARY entries and deliberately resets between SUMMARY and FULL [CC-WEB-001]", async () => {
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
      fields={definition.descriptors.fields}
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
