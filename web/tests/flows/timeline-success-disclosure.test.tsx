import { act } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { HistoryDisclosure, Screen } from "@contracts/client";
import { render, screen } from "../support/presentation-test-support";
import { workBasis } from "../support/inputs";
import { humanReadHttpReply } from "../support/human-read-reply.fixtures";
import { CaseTimeline } from "../../src/views/case/CaseTimeline";
const requestBody = (init?: RequestInit): string => {
  if (typeof init?.body !== "string") {
    throw Error("Serialized request required");
  }
  return init.body;
};
const supplied = (text: string, count: number): Screen => ({
  regions: [
    {
      role: "RESULTS",
      content: {
        kind: "TABLE",
        columns: [],
        sections: [
          {
            titleId: "label.changes",
            entries: [
              {
                key: "future-value",
                labelId: "label.changes",
                prominence: "PRIMARY",
                value: { kind: "TEXT", value: text },
              },
            ],
          },
        ],
        rows: Array.from({ length: count }, () => []).map((values, index) => ({
          key: String(index),
          values,
        })),
      },
    },
  ],
});
it.each([
  ["FULL", false],
  ["SUMMARY", false],
  ["ERASED", false],
  ["FULL", true],
  ["SUMMARY", true],
  ["ERASED", true],
] as const)(
  "retains only authorized ordinary timeline pages after %s success (empty=%s)",
  async (level, empty) => {
    const changed = vi.fn();
    const inputs: unknown[] = [];
    const reply = (
      disclosure: HistoryDisclosure,
      canary: string,
      cursor: string | null,
      count: number,
    ) =>
      humanReadHttpReply("case.timeline", {
        screen: supplied(canary, count),
        context: { kind: "PAGE", tasks: [], nextCursor: cursor },
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure,
      });
    const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((_path, init) => {
      inputs.push(JSON.parse(requestBody(init)));
      return Promise.resolve(
        inputs.length === 1
          ? reply("FULL", "FULL_OLD_CANARY", "first-range", 1)
          : reply(
              level,
              `NEW_${inputs.length}`,
              inputs.length === 2 ? "last-range" : null,
              empty ? 0 : 1,
            ),
      );
    });
    vi.stubGlobal("fetch", fetch);
    const user = userEvent.setup();
    render(
      <CaseTimeline
        target={{ kind: "REFERENCE", caseReference: "CASE-1" }}
        onDisclosureChanged={changed}
      />,
    );
    await screen.findByText("FULL_OLD_CANARY");
    await user.click(screen.getByRole("button", { name: "Load more" }));
    await screen.findByText("NEW_2");
    expect(screen.queryByText("FULL_OLD_CANARY") !== null).toBe(level === "FULL");
    expect(changed).toHaveBeenCalledTimes(level === "FULL" ? 0 : 1);
    await user.click(screen.getByRole("button", { name: "Load more" }));
    await screen.findByText("NEW_3");
    expect(screen.queryByText("NEW_2")).not.toBeNull();
    expect(inputs).toEqual([
      { target: { kind: "REFERENCE", caseReference: "CASE-1" }, limit: 50 },
      { target: { kind: "REFERENCE", caseReference: "CASE-1" }, cursor: "first-range", limit: 50 },
      { target: { kind: "REFERENCE", caseReference: "CASE-1" }, cursor: "last-range", limit: 50 },
    ]);
    expect(changed).toHaveBeenCalledTimes(level === "FULL" ? 0 : 1);
  },
);

it("ignores an obsolete owner's successful downgrade without clearing the replacement", async () => {
  const late = Promise.withResolvers<Response>();
  const changed = vi.fn();
  let count = 0;
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((_url, init) => {
    count += 1;
    const input = JSON.parse(requestBody(init)) as {
      target: { caseReference: string };
      cursor?: string;
    };
    if (input.cursor !== undefined) {
      return late.promise;
    }
    const first = input.target.caseReference === "CASE-1";
    return Promise.resolve(
      humanReadHttpReply("case.timeline", {
        screen: supplied(first ? "OLD_OWNER_FULL" : "NEW_OWNER_FULL", 1),
        context: { kind: "PAGE", tasks: [], nextCursor: first ? "old-range" : null },
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure: "FULL",
      }),
    );
  });
  vi.stubGlobal("fetch", fetch);
  const user = userEvent.setup();
  const view = render(
    <CaseTimeline
      target={{ kind: "REFERENCE", caseReference: "CASE-1" }}
      onDisclosureChanged={changed}
    />,
  );
  await screen.findByText("OLD_OWNER_FULL");
  await user.click(screen.getByRole("button", { name: "Load more" }));
  view.rerender(
    <CaseTimeline
      target={{ kind: "REFERENCE", caseReference: "CASE-2" }}
      onDisclosureChanged={changed}
    />,
  );
  await screen.findByText("NEW_OWNER_FULL");
  late.resolve(
    humanReadHttpReply("case.timeline", {
      screen: supplied("OBSOLETE_SUMMARY", 0),
      context: { kind: "PAGE", tasks: [], nextCursor: null },
      basis: { kind: "SNAPSHOT", observation: workBasis },
      disclosure: "SUMMARY",
    }),
  );
  await act(async () => {
    await late.promise;
  });
  expect(screen.queryByText("OBSOLETE_SUMMARY")).toBeNull();
  expect(screen.getByText("NEW_OWNER_FULL")).toBeVisible();
  expect(changed).not.toHaveBeenCalled();
  expect(count).toBe(3);
});
