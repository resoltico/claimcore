import { caseViewInput, caseSummaryInput } from "../support/inputs";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "../support/presentation-test-support";
import { preferenceKey } from "../../src/presentation/preferences";
import { CaseDetail } from "../../src/views/CaseDetail";
import { CaseList } from "../../src/views/CaseList";
import { BusinessValue } from "../../src/components/BusinessValue";
import { CopyValue } from "../../src/components/CopyValue";
import { definition, fields, response, context, unavailable } from "../support/service-ui.fixtures";
import { historyVariants } from "../support/contract-corpus.fixtures";
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
      version: 2,
      language,
      displayLocale: "lv-LV",
      theme: "SYSTEM" as const,
    }),
  );
};
const historyReply = (value: string) => {
  const { full } = historyVariants();
  const entries = [
    {
      kind: "FULL" as const,
      receipt: {
        ...full.receipt,
        subject: {
          ...full.receipt.subject,
          snapshot: {
            kind: "DISCLOSED",
            view: {
              kind: "ACTIVE",
              fields: {
                ...fields,
                caseReference: value,
              },
            },
          },
        },
      },
    },
  ];
  return {
    nextCursor: "next",
    entries,
  };
};
const readReply = (url: string, value: string, missing: boolean) => {
  if (url.endsWith("/context")) {
    return response("context", "SUCCEEDED", context);
  }
  if (url.endsWith("/case.list")) {
    return response("case.list", "SUCCEEDED", {
      offers: context.offers,
      items: [
        caseSummaryInput({
          state: { kind: "ACTIVE", status: "OPENED" },
          caseReference: value,
          revision: 1,
        }),
      ],
      nextCursor: null,
    });
  }
  if (url.endsWith("/case.history")) {
    return response(
      "case.history",
      missing ? "REJECTED" : "SUCCEEDED",
      missing ? unavailable : historyReply(value),
    );
  }
  return response(
    "case.get",
    missing ? "REJECTED" : "SUCCEEDED",
    missing
      ? unavailable
      : caseViewInput({
          caseReference: value,
          revision: 1,
          advice: {
            acts: [],
            history: ["SUMMARY", "FULL"],
          },
          state: {
            kind: "ACTIVE",
            fields: {
              ...fields,
              caseReference: value,
            },
          },
        }),
  );
};
const checkDetail = async (value: string, points: string | null, missing: boolean) => {
  vi.stubGlobal(
    "fetch",
    vi.fn((url: string) => Promise.resolve(readReply(url, value, missing))),
  );
  const view = render(
    <CaseDetail
      caseReference={value}
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />,
  );
  assertSafe(view.container.querySelector(".section-heading")!, value, points);
  if (!missing) {
    await waitFor(() => {
      expect(view.container.querySelector("select")).not.toBeNull();
    });
    fireEvent.change(view.container.querySelector("select")!, {
      target: {
        value: "FULL",
      },
    });
  }
  await waitFor(() => {
    expect(view.container.querySelectorAll('.history-list > p[role="status"]')).toHaveLength(0);
    const directStatus = view.container.querySelectorAll(
      'section[aria-labelledby="case-detail-title"] > .case-lifecycle p[role="status"]',
    );
    expect(directStatus).toHaveLength(missing ? 0 : 1);
    if (missing) {
      expect(view.container.querySelector(".case-fields")).toBeNull();
    } else {
      expect(view.container.querySelector(".history-list li")).not.toBeNull();
    }
  });
  if (missing) {
    expect(view.container.querySelector(".case-fields, .history-list li")).toBeNull();
    expect(view.container).not.toHaveTextContent(fields.registration.claimantName);
    expect(view.container.querySelector("#acts-title")).toBeNull();
  } else {
    assertSafe(view.container.querySelector(".case-fields")!, value, points);
    assertSafe(view.container.querySelector(".history-list li")!, value, points);
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
      const list = render(<CaseList onSelect={vi.fn()} onOpen={vi.fn()} />);
      await waitFor(() => {
        expect(list.container.querySelector(".worklist-row")).not.toBeNull();
      });
      assertSafe(list.container.querySelector(".worklist-row")!, value, points);
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
