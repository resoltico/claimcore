import type { CatalogDocument } from "@contracts/client";
import type { CatalogInput } from "../../../../src/protocol/catalogAssembly";
import type * as Metadata from "../../../../src/protocol/productMetadata";
import type * as LanguageCatalog from "../../../../src/presentation/languageCatalog";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { mockEndpoint } from "../../../support/endpoint-mocks";
import { workBasis } from "../../../support/inputs";
import { ReminderQueue } from "../../../../src/views/work/ReminderQueue";
import { navigate } from "../../../../src/routing/navigation";

vi.mock("../../../../src/protocol/productMetadata", async (original) => {
  const actual = await original<typeof Metadata>();
  const entries: CatalogDocument["entries"][number][] = [];
  for (const entry of actual.productMetadata.catalog.entries) {
    if (entry.endpoint === "reminder.list") {
      const inputs: CatalogInput[] = [];
      for (const input of entry.inputs) {
        if (input.kind.kind === "CHOICE" && input.kind.vocabulary === "reminderState") {
          inputs.push({
            ...input,
            labelId: "label.details",
            kind: { ...input.kind, options: ["CANCELLED", "PENDING"] },
          });
        } else {
          inputs.push(input);
        }
      }
      entries.push({ ...entry, inputs });
    } else {
      entries.push(entry);
    }
  }
  return {
    ...actual,
    productMetadata: {
      ...actual.productMetadata,
      catalog: { ...actual.productMetadata.catalog, entries },
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
  const reads = mockEndpoint("reminder.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "SUCCEEDED",
        view: { regions: [] },
        data: { basis: workBasis, items: [], nextCursor: null },
      },
    },
  });
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
    expect(reads.mock.calls.at(-1)?.[1]).toEqual({ state: "CANCELLED", limit: 50 });
  });
});
