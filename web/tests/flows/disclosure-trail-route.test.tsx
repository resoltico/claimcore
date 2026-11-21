import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { navigate } from "../../src/routing/navigation";
import { Dashboard } from "../../src/views/Dashboard";
import { chooseOption } from "../support/choice-entry";
import { stubViewCounts } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { humanReadHttpReply, humanReadReply } from "../support/human-read-reply.fixtures";
import { activeRequester, requesterLinks } from "../support/human-requester.fixtures";
import { requestText } from "../support/transport-response.fixtures";
import { workBasis } from "../support/inputs";
import { render, screen } from "../support/presentation-test-support";

const actorId = "10000000-0000-7000-8000-000000000001";
const caseId = "20000000-0000-7000-8000-000000000001";
const from = "2026-11-04T10:30:00.000000Z";
const until = "2026-11-04T11:30:00.000000Z";

const page = (recordedCase: string, nextCursor: string | null = null) =>
  humanReadHttpReply("disclosure.trail", {
    screen: {
      regions: [
        {
          role: "RESULTS",
          content: {
            kind: "TABLE",
            rows: [
              {
                key: recordedCase,
                values: [{ kind: "IDENTIFIER", value: recordedCase }],
                actions: [],
              },
            ],
            columns: [
              {
                key: "caseId",
                labelId: "input.caseId.label",
                prominence: "PRIMARY",
                isMoney: false,
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
    nextCursor,
    basis: { kind: "SNAPSHOT", observation: workBasis },
    disclosure: "CURRENT",
  });

const showTrail = (allowed: boolean) => {
  stubViewCounts();
  mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("context", {
      ...activeRequester,
      basis: { kind: "SNAPSHOT", observation: workBasis },
      links: allowed
        ? [
            {
              rel: "VIEW",
              labelId: "endpoint.disclosure.trail.label",
              link: { endpoint: "disclosure.trail", input: { limit: 50 } },
            },
          ]
        : requesterLinks(["OPERATIONS"], []),
    }),
  });
  navigate("/disclosure-trail", true);
  render(<Dashboard />);
};

afterEach(() => vi.unstubAllGlobals());

it("submits the applied trail filters on all pages and replaces results when filters change", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(page("unfiltered-case"))
    .mockResolvedValueOnce(page("selected-case", "older-events"))
    .mockResolvedValueOnce(page("older-case"));
  vi.stubGlobal("fetch", fetch);
  showTrail(true);
  const user = userEvent.setup();
  await screen.findByText("unfiltered-case");

  await user.type(screen.getByRole("textbox", { name: "Account ID" }), actorId);
  await user.type(screen.getByRole("textbox", { name: "Case ID" }), caseId);
  await chooseOption(user, screen.getByRole("combobox", { name: "Event kind" }), "CASE_READ");
  await user.type(screen.getByRole("textbox", { name: "From (inclusive)" }), from);
  await user.type(screen.getByRole("textbox", { name: "Until (exclusive)" }), until);
  await user.click(screen.getByRole("button", { name: "Apply filters" }));

  await screen.findByText("selected-case");
  expect(screen.queryByText("unfiltered-case")).toBeNull();
  await user.click(screen.getByRole("button", { name: "Load more events" }));
  expect(await screen.findByText("older-case")).toBeVisible();
  expect(screen.getByText("selected-case")).toBeVisible();

  const actual = fetch.mock.calls.map(([path, options]) => {
    expect(path).toBe("/api/disclosure.trail");
    expect(options?.method).toBe("POST");
    return JSON.parse(requestText(options?.body)) as unknown;
  });
  const selection = { actorId, caseId, kind: "CASE_READ", from, until, limit: 50 };
  expect(actual).toEqual([{ limit: 50 }, selection, { ...selection, cursor: "older-events" }]);
});

it("shows a refusal for an unoffered trail and sends no query", async () => {
  const fetch = vi.fn<typeof globalThis.fetch>();
  vi.stubGlobal("fetch", fetch);
  showTrail(false);
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "This item does not exist, or you do not have access to it.",
  );
  expect(screen.queryByRole("link", { name: "Access and export log" })).toBeNull();
  expect(fetch).not.toHaveBeenCalled();
});
