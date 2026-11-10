import type * as TaskCatalog from "../../../../src/protocol/taskCatalog";
import type * as LanguageCatalog from "../../../../src/presentation/languageCatalog";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { afterEach } from "vitest";
afterEach(() => vi.unstubAllGlobals());
import { ReminderQueue } from "../../../../src/views/work/ReminderQueue";
import { navigate } from "../../../../src/routing/navigation";

vi.mock("../../../../src/protocol/taskCatalog", async (original) => {
  const actual = await original<typeof TaskCatalog>();
  return {
    ...actual,
    queryInput: (
      task: Parameters<typeof actual.queryInput>[0],
      input: Parameters<typeof actual.queryInput>[1],
    ) => {
      const value = actual.queryInput(task, input);
      return task === "query:reminderList" && input === "input:reminderState"
        ? { ...value, labelId: "label.details", choices: ["CANCELLED", "PENDING"] }
        : value;
    },
  };
});

vi.mock("../../../../src/presentation/languageCatalog", async (original) => {
  const actual = await original<typeof LanguageCatalog>();
  return {
    ...actual,
    catalogFor: (language: Parameters<typeof actual.catalogFor>[0]) => ({
      ...actual.catalogFor(language),
      "label.reminders": "Core reminder canopy Ω",
    }),
  };
});

it("uses the core query selection, order, input label and shared wording in the actual queue", async () => {
  const reads = vi.fn<typeof globalThis.fetch>().mockImplementation(
    async () =>
      new Response(
        JSON.stringify({
          representation: "HUMAN",
          outcome: {
            kind: "SUCCEEDED",
            document: {
              screen: { regions: [] },
              context: { kind: "REMINDERS", reminders: [], nextCursor: null },
              basis: {
                kind: "SNAPSHOT",
                observation: {
                  observedAt: "2026-11-09T12:00:00.000000Z",
                  businessDate: "2026-11-09",
                  timeZone: "Etc/UTC",
                },
              },
              disclosure: "CURRENT",
            },
          },
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
  );
  vi.stubGlobal("fetch", reads);
  navigate("/reminders?filters=manual", true);
  render(<ReminderQueue />);
  expect(await screen.findByRole("heading", { name: "Core reminder canopy Ω" })).toBeVisible();
  const user = userEvent.setup();
  await user.click(screen.getByRole("combobox", { name: "Details" }));
  expect(screen.getAllByRole("option").map((option) => option.textContent)).toEqual([
    "Any",
    "Cancelled",
    "Pending",
  ]);
  await user.click(screen.getByRole("option", { name: "Cancelled" }));
  await waitFor(() => {
    expect(JSON.parse(String(reads.mock.calls.at(-1)?.[1]?.body))).toEqual({
      state: "CANCELLED",
      limit: 50,
    });
  });
});
