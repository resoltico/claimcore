import { requestAddress } from "../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, testWorkspaceIdentity } from "../../support/presentation-test-support";
import { WorkspaceHeader } from "../../../src/views/WorkspaceHeader";
import { RecoveryProvider } from "../../../src/recovery/RecoveryContext";
import { WorkspaceViewsProvider } from "../../../src/views/WorkspaceViews";
import { CaseList } from "../../../src/views/CaseList";
import { ReminderQueue } from "../../../src/views/work/ReminderQueue";
import { navigate } from "../../../src/routing/navigation";
import type { HumanReadDocument_case_list, HumanReadDocument_work_views } from "@contracts/client";
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
const counts: HumanReadDocument_work_views = {
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "count",
              labelId: "label.total",
              prominence: "SECONDARY",
              isMoney: false,
            },
            {
              key: "view",
              labelId: "label.views",
              prominence: "PRIMARY",
              isMoney: false,
            },
          ],
          rows: [
            {
              key: "case-choice",
              link: { endpoint: "case.list", input: { view: "ALL", limit: 20 } },
              values: [
                {
                  kind: "COUNT",
                  value: 17,
                },
                {
                  kind: "TEXT",
                  value: "Core case tab Ω",
                },
              ],
            },
            {
              key: "reminder-choice",
              link: { endpoint: "reminder.list", input: { view: "PENDING", limit: 20 } },
              values: [
                {
                  kind: "COUNT",
                  value: 23,
                },
                {
                  kind: "TEXT",
                  value: "Core reminder tab Ψ",
                },
              ],
            },
          ],
        },
      },
    ],
  },
  subject: { kind: "LIST" },
  actions: [],
  links: [],
  history: null,
  nextCursor: null,
  basis,
  disclosure: "CURRENT",
};
const cases: HumanReadDocument_case_list = {
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
          rows: [
            {
              key: caseId,
              link: { endpoint: "case.get", input: { target: { kind: "ID", caseId } } },
              values: [
                {
                  kind: "TEXT",
                  value: "Core listed case Χ",
                },
              ],
            },
          ],
          sections: [
            {
              titleId: "label.exactReference",
              entries: [
                {
                  key: "exactReference",
                  labelId: "field.caseReference.label",
                  prominence: "PRIMARY",
                  value: {
                    kind: "TEXT",
                    value: "Core exact case Δ",
                  },
                },
              ],
            },
          ],
        },
      },
    ],
  },
  subject: { kind: "LIST" },
  actions: [],
  links: [
    {
      rel: "OPEN_CASE",
      labelId: "field.caseReference.label",
      link: { endpoint: "case.get", input: { target: { kind: "ID", caseId: exactId } } },
    },
  ],
  history: null,
  nextCursor: null,
  basis,
  disclosure: "CURRENT",
};
const reply = (document: unknown) => ({
  representation: "HUMAN",
  outcome: {
    kind: "SUCCEEDED",
    document,
  },
});
const response = (document: unknown) =>
  new Response(JSON.stringify(reply(document)), {
    status: 200,
    headers: {
      "Content-Type": "application/json",
    },
  });
afterEach(() => vi.unstubAllGlobals());
const service = () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((url) =>
    Promise.try(() => {
      switch (requestAddress(url)) {
        case "/api/work.views":
          return response(counts);
        case "/api/case.list":
          return response(cases);
        case "/api/reminder.list":
          return response({
            screen: {
              regions: [],
            },
            subject: { kind: "LIST" },
            actions: [],
            links: [],
            history: null,
            nextCursor: null,
            basis,
            disclosure: "CURRENT",
          });
        case "/api/case.summary":
          return response({
            screen: {
              regions: [],
            },
            subject: { kind: "LIST" },
            actions: [],
            links: [],
            history: null,
            nextCursor: null,
            basis,
            disclosure: "CURRENT",
          });
        default:
          throw new Error(`Unexpected endpoint ${requestAddress(url)}`);
      }
    }),
  );
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
  await screen.findByRole("tab", {
    name: "Core case tab Ω 17",
  });
  const row = await screen.findByRole("link", {
    name: "Core listed case Χ",
  });
  await userEvent.click(row);
  expect(select).toHaveBeenLastCalledWith({
    kind: "ID",
    caseId,
  });
  await userEvent.click(
    screen.getByRole("link", {
      name: "Core exact case Δ",
    }),
  );
  expect(select).toHaveBeenLastCalledWith({
    kind: "ID",
    caseId: exactId,
  });
  expect(
    screen.queryByRole("button", {
      name: "Reassign",
    }),
  ).not.toBeInTheDocument();
});
it("renders the supplied reminder tab and keeps the server-declared manual filter values", async () => {
  const fetch = service();
  navigate("/reminders?view=PENDING", true);
  render(
    <WorkspaceViewsProvider>
      <ReminderQueue />
    </WorkspaceViewsProvider>,
  );
  await screen.findByRole("tab", {
    name: "Core reminder tab Ψ 23",
  });
  await userEvent.click(
    screen.getByRole("button", {
      name: /manual filters/iu,
    }),
  );
  expect(
    screen.getByRole("combobox", {
      name: /reminder state/iu,
    }),
  ).toBeVisible();
  expect(fetch.mock.calls.some(([url]) => requestAddress(url).endsWith("reminder.list"))).toBe(
    true,
  );
});
it("renders Core navigation totals instead of adding tab counts in the shell", async () => {
  const row = (key: string, label: string, count: number) => ({
    key,
    values: [
      {
        kind: "COUNT" as const,
        value: count,
      },
      {
        kind: "TEXT" as const,
        value: label,
      },
    ],
  });
  const navigation: HumanReadDocument_work_views = {
    ...counts,
    screen: {
      regions: [
        {
          role: "TOTALS",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "cases",
                labelId: "label.cases",
                prominence: "PRIMARY",
                value: {
                  kind: "COUNT",
                  value: 5,
                },
              },
              {
                key: "reminders",
                labelId: "label.reminders",
                prominence: "PRIMARY",
                value: {
                  kind: "COUNT",
                  value: 3,
                },
              },
            ],
          },
        },
        {
          role: "RESULTS",
          content: {
            kind: "TABLE",
            columns: [
              {
                key: "count",
                labelId: "label.total",
                prominence: "SECONDARY",
                isMoney: false,
              },
              {
                key: "view",
                labelId: "label.views",
                prominence: "PRIMARY",
                isMoney: false,
              },
            ],
            rows: [
              {
                ...row("all", "Core all cases", 17),
                link: { endpoint: "case.list", input: { view: "ALL", limit: 20 } },
              },
              {
                ...row("attention", "Core attention cases", 5),
                link: { endpoint: "case.list", input: { view: "NEEDS_ATTENTION", limit: 20 } },
              },
              {
                ...row("pending", "Core pending reminders", 23),
                link: { endpoint: "reminder.list", input: { view: "PENDING", limit: 20 } },
              },
              {
                ...row("overdue", "Core overdue reminders", 1),
                link: { endpoint: "reminder.list", input: { view: "OVERDUE", limit: 20 } },
              },
              {
                ...row("today", "Core reminders today", 2),
                link: { endpoint: "reminder.list", input: { view: "TODAY", limit: 20 } },
              },
            ],
          },
        },
      ],
    },
    subject: { kind: "LIST" },
    actions: [],
    links: [],
    history: null,
    nextCursor: null,
  };
  vi.stubGlobal("fetch", vi.fn<typeof globalThis.fetch>().mockResolvedValue(response(navigation)));
  render(
    <RecoveryProvider identity={testWorkspaceIdentity}>
      <WorkspaceViewsProvider>
        <WorkspaceHeader
          route={{
            screen: "cases",
            query: "",
          }}
          presentation={null}
          active="cases"
          views={["case.list"]}
          tasks={[]}
          locked={false}
          openCase={vi.fn()}
        />
      </WorkspaceViewsProvider>
    </RecoveryProvider>,
  );
  expect(
    (
      await screen.findAllByRole("link", {
        name: "Cases 5",
      })
    ).length,
  ).toBeGreaterThan(0);
  expect(
    screen.getAllByRole("link", {
      name: "Reminders 3",
    }).length,
  ).toBeGreaterThan(0);
  expect(
    screen.queryByRole("link", {
      name: "Cases 17",
    }),
  ).not.toBeInTheDocument();
  expect(
    screen.queryByRole("link", {
      name: "Reminders 23",
    }),
  ).not.toBeInTheDocument();
});
