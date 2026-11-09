import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../support/presentation-test-support";
import { WorkspaceViewsProvider } from "../../../src/views/WorkspaceViews";
import { CaseList } from "../../../src/views/CaseList";
import { ReminderQueue } from "../../../src/views/work/ReminderQueue";
import { navigate } from "../../../src/routing/navigation";
import {
  isResponse,
  type HumanReadDocument_caseList,
  type HumanReadDocument_workViews,
} from "@contracts/client";

const caseId = "00000000-0000-4000-8000-000000000001";
const exactId = "00000000-0000-4000-8000-000000000002";
const basis = {
  kind: "SNAPSHOT",
  observation: {
    observedAt: "2026-11-09T12:00:00.000000Z",
    businessDate: "2026-11-09",
    timeZone: "Etc/UTC",
  },
} as const;
const counts: HumanReadDocument_workViews = {
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            { key: "count", labelId: "label.total", prominence: "SECONDARY", isMoney: false },
            { key: "view", labelId: "label.views", prominence: "PRIMARY", isMoney: false },
          ],
          rows: [
            {
              key: "case-choice",
              values: [
                { kind: "COUNT", value: 17 },
                { kind: "TEXT", value: "Core case tab Ω" },
              ],
            },
            {
              key: "reminder-choice",
              values: [
                { kind: "COUNT", value: 23 },
                { kind: "TEXT", value: "Core reminder tab Ψ" },
              ],
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "WORK_VIEWS",
    cases: [{ key: "case-choice", view: "ALL" }],
    reminders: [{ key: "reminder-choice", view: "PENDING" }],
  },
  basis,
  disclosure: "CURRENT",
};
const cases: HumanReadDocument_caseList = {
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "caseReference",
              labelId: "field.caseReference.label",
              prominence: "PRIMARY",
              isMoney: false,
            },
          ],
          rows: [{ key: caseId, values: [{ kind: "TEXT", value: "Core listed case Χ" }] }],
          sections: [
            {
              titleId: "label.exactReference",
              entries: [
                {
                  key: "exactReference",
                  labelId: "field.caseReference.label",
                  prominence: "PRIMARY",
                  value: { kind: "TEXT", value: "Core exact case Δ" },
                },
              ],
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "CASES",
    cases: [{ caseId, assignment: null }],
    exactCaseId: exactId,
    tasks: [],
    nextCursor: null,
  },
  basis,
  disclosure: "CURRENT",
};
const reply = (document: unknown) => ({
  representation: "HUMAN",
  outcome: { kind: "SUCCEEDED", document },
});
const response = (document: unknown) =>
  new Response(JSON.stringify(reply(document)), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
afterEach(() => vi.unstubAllGlobals());
const service = () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async (url) => {
    switch (String(url)) {
      case "/api/work.views":
        return response(counts);
      case "/api/case.list":
        return response(cases);
      case "/api/reminder.list":
        return response({
          screen: { regions: [] },
          context: { kind: "REMINDERS", reminders: [], nextCursor: null },
          basis,
          disclosure: "CURRENT",
        });
      case "/api/case.summary":
        return response({
          screen: { regions: [] },
          context: { kind: "EMPTY" },
          basis,
          disclosure: "CURRENT",
        });
      default:
        throw new Error(`Unexpected endpoint ${String(url)}`);
    }
  });
  vi.stubGlobal("fetch", fetch);
  return fetch;
};
it("uses supplied tab labels/counts and separate typed row/exact-match destinations without inferring them from search text", async () => {
  service();
  navigate("/cases?view=ALL&q=unrelated", true);
  const select = vi.fn();
  render(
    <WorkspaceViewsProvider>
      <CaseList onSelect={select} />
    </WorkspaceViewsProvider>,
  );
  await screen.findByRole("tab", { name: "Core case tab Ω 17" });
  const row = await screen.findByRole("link", { name: "Core listed case Χ" });
  await userEvent.click(row);
  expect(select).toHaveBeenLastCalledWith({ kind: "ID", caseId });
  await userEvent.click(screen.getByRole("link", { name: "Core exact case Δ" }));
  expect(select).toHaveBeenLastCalledWith({ kind: "ID", caseId: exactId });
  expect(screen.queryByRole("button", { name: "Reassign" })).not.toBeInTheDocument();
});
it("renders the supplied reminder tab and keeps the server-declared manual filter values", async () => {
  const fetch = service();
  navigate("/reminders?view=PENDING", true);
  render(
    <WorkspaceViewsProvider>
      <ReminderQueue />
    </WorkspaceViewsProvider>,
  );
  await screen.findByRole("tab", { name: "Core reminder tab Ψ 23" });
  await userEvent.click(screen.getByRole("button", { name: /manual filters/i }));
  expect(screen.getByRole("combobox", { name: /reminder state/i })).toBeVisible();
  expect(fetch.mock.calls.some(([url]) => String(url).endsWith("reminder.list"))).toBe(true);
});
it("refuses missing, duplicated and mismatched workspace view controls and stale exact-match destinations", () => {
  expect(isResponse("work.views", reply(counts))).toBe(true);
  for (const context of [
    { ...counts.context, cases: [] },
    { ...counts.context, cases: [...counts.context.cases, ...counts.context.cases] },
    { ...counts.context, cases: [{ key: "unbound", view: "ALL" }] },
  ]) {
    expect(isResponse("work.views", reply({ ...counts, context }))).toBe(false);
  }
  expect(isResponse("case.list", reply(cases))).toBe(true);
  expect(
    isResponse(
      "case.list",
      reply({
        ...cases,
        screen: { regions: [] },
        disclosure: "WITHHELD",
        context: { ...cases.context, cases: [] },
      }),
    ),
  ).toBe(false);
});
