import { endpointResponse } from "../support/transport-response.fixtures";
import { humanReadHttpReply } from "../support/human-read-reply.fixtures";
import { activeRequester } from "../support/human-requester.fixtures";
import { caseDocumentInput } from "../support/case-document.fixtures";
import type { Screen } from "@contracts/client";
import { stubWorklistSummary } from "../support/worklist-summary.fixtures";
import { workBasis } from "../support/inputs";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { preferenceKey } from "../../src/presentation/preferences";
import { CaseDetail } from "../../src/views/CaseDetail";
import { CaseList } from "../../src/views/CaseList";
import { BusinessValue } from "../../src/components/BusinessValue";
import { CopyValue } from "../../src/components/CopyValue";
import { definition, fields } from "../support/service-ui.fixtures";
const languages = ["en", "lv", "ar", "en-XA"] as const;
const values = [
  {
    value: "ABC\u202eDEF\u202d\u202c",
    points: "U+202E, U+202D, U+202C",
  },
  {
    value: "ABC\u2069DEF\u2066\u2067\u2068",
    points: "U+2069, U+2066, U+2067, U+2068",
  },
  {
    value: "A\u0308 العربية\u200d\u200c <script>",
    points: "U+200D, U+200C",
  },
  {
    value: "A\u0308 <b>literal</b>",
    points: null,
  },
];
const assertSafe = (root: HTMLElement, value: string, points: string | null) => {
  expect([...root.querySelectorAll("bdi")].some((node) => node.textContent === value)).toBe(true);
  if (points === null) {
    expect(root.querySelector(".character-warning")).toBeNull();
  } else {
    expect(root).toHaveTextContent(points);
  }
  expect(root.querySelector("script, b")).toBeNull();
};
const preferences = (language: (typeof languages)[number]) => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({
      version: 3,
      language,
      displayLocale: "lv-LV",
      mode: "system" as const,
      accent: "green" as const,
    }),
  );
};
const referenceRows = (value: string): Screen => ({
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
            key: "20000000-0000-4000-8000-000000000001",
            link: {
              endpoint: "case.get",
              input: { target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" } },
            },
            values: [{ kind: "REFERENCE", value }],
          },
        ],
      },
    },
  ],
});
const readReply = (url: string, value: string, missing: boolean) => {
  if (url.endsWith("/context")) {
    return humanReadHttpReply("context", activeRequester);
  }
  if (url.endsWith("/case.list")) {
    return humanReadHttpReply("case.list", {
      screen: referenceRows(value),
      subject: { kind: "LIST" },
      actions: [],
      links: [],
      history: null,
      nextCursor: null,
      basis: { kind: "SNAPSHOT", observation: workBasis },
      disclosure: "CURRENT",
    });
  }
  const timeline = url.endsWith("/case.timeline");
  if (missing) {
    return endpointResponse(timeline ? "case.timeline" : "case.get", {
      representation: "HUMAN",
      outcome: {
        kind: "REJECTED",
        rejection: {
          kind: "REFUSED",
          advice: {
            message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
            action: "ASK_FOR_ACCESS",
          },
        },
      },
    });
  }
  if (timeline) {
    return humanReadHttpReply("case.timeline", {
      screen: referenceRows(value),
      subject: { kind: "LIST" },
      actions: [],
      links: [],
      history: null,
      nextCursor: "next",
      basis: { kind: "SNAPSHOT", observation: workBasis },
      disclosure: "FULL",
    });
  }
  return humanReadHttpReply(
    "case.get",
    caseDocumentInput(
      { history: "FULL" },
      {
        regions: [
          {
            role: "IDENTITY",
            content: {
              kind: "ENTRIES",
              entries: [
                {
                  key: "caseReference",
                  labelId: "field.caseReference.label",
                  prominence: "PRIMARY",
                  value: { kind: "REFERENCE", value },
                },
                {
                  key: "state",
                  labelId: "label.status",
                  prominence: "PRIMARY",
                  value: { kind: "STATE", vocabulary: "headline", token: "UNDECIDED" },
                },
              ],
            },
          },
        ],
      },
    ),
  );
};
const checkDetail = async (value: string, points: string | null, missing: boolean) => {
  vi.stubGlobal(
    "fetch",
    vi.fn((url: string) => Promise.resolve(readReply(url, value, missing))),
  );
  const view = render(
    <CaseDetail
      target={{ kind: "REFERENCE", caseReference: value }}
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />,
  );
  assertSafe(view.container.querySelector(".case-header-card")!, value, points);

  await waitFor(() => {
    expect(view.container.querySelectorAll('.case-timeline > p[role="status"]')).toHaveLength(0);
    const directStatus = view.container.querySelectorAll(".case-header-state .badge");
    expect(directStatus).toHaveLength(missing ? 0 : 1);
    if (missing) {
      expect(
        view.container.querySelector('[data-region="FACTS"] [data-entry="claimantName"]'),
      ).toBeNull();
    } else {
      expect(
        view.container.querySelector(".case-timeline-panel .table-view tbody > tr"),
      ).not.toBeNull();
    }
  });
  if (missing) {
    expect(
      view.container.querySelector(
        '[data-region="FACTS"] [data-entry="claimantName"], .case-timeline-panel .table-view tbody > tr',
      ),
    ).toBeNull();
    expect(view.container).not.toHaveTextContent(fields.registration.claimantName);
    expect(view.container.querySelector("#acts-title")).toBeNull();
  } else {
    assertSafe(view.container.querySelector("[data-region=IDENTITY]")!, value, points);
    assertSafe(
      view.container.querySelector(".case-timeline-panel .table-view tbody > tr")!,
      value,
      points,
    );
  }
  view.unmount();
};
it.each(languages)(
  "protects ordinary list, detail, history and absent lookup references in %s",
  async (language) => {
    preferences(language);
    for (const { value, points } of [
      {
        value: "<script>",
        points: null,
      },
    ]) {
      vi.stubGlobal(
        "fetch",
        vi.fn((url: string) => Promise.resolve(readReply(url, value, false))),
      );
      const list = render(<CaseList onSelect={vi.fn()} />);
      await waitFor(() => {
        expect(list.container.querySelector(".table-view tbody > tr")).not.toBeNull();
      });
      assertSafe(list.container.querySelector(".table-view tbody > tr")!, value, points);
      list.unmount();
      await checkDetail(value, points, false);
      await checkDetail(value, points, true);
    }
  },
);
it.each(languages)(
  "keeps before/after words outside business direction and exact clipboard fallback in %s",
  async (language) => {
    preferences(language);
    const user = userEvent.setup();
    const writeText = vi.fn().mockRejectedValue(new Error("unavailable"));
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: {
        writeText,
      },
    });
    const { value, points } = values[0]!;
    const field = definition.catalog.fields.find((item) => item.name === "claimantName")!;
    const view = render(
      <>
        <BusinessValue value={value} field={field} context="ui.after" />
        <CopyValue value={value} label="synthetic reference" />
      </>,
    );
    assertSafe(view.container, value, points);
    expect(view.container.querySelector("bdi")!.textContent).toBe(value);
    await user.click(screen.getByRole("button"));
    const fallback = await screen.findByRole("textbox");
    expect(fallback).toHaveValue(value);
    expect(writeText).toHaveBeenCalledWith(value);
    expect(view.container.querySelectorAll(".character-warning")).toHaveLength(2);
  },
);

beforeEach(() => {
  stubWorklistSummary();
});
