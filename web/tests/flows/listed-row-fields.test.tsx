import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { caseSummaryInput, listedFieldsInput } from "../support/inputs";
afterEach(() => {
  vi.doUnmock("@contracts/client");
  vi.doUnmock("../../src/protocol/productMetadata");
});
it("renders an additional listed date from core declarations without adding a bespoke row interface", async () => {
  const actual = await import("@contracts/client");
  const { productMetadata } = await import("../../src/protocol/productMetadata");
  const fields = [...productMetadata.catalog.fields];
  const index = fields.findIndex((field) => field.name === "incidentDate");
  const date = fields[index];
  if (date === undefined) {
    throw new Error("Required incident date descriptor is absent.");
  }
  fields[index] = { ...date, listed: true };
  vi.doMock("@contracts/client", () => ({
    ...actual,
    listedCaseFieldNames: [...actual.listedCaseFieldNames, "incidentDate"],
  }));
  vi.doMock("../../src/protocol/productMetadata", () => ({
    productMetadata: {
      ...productMetadata,
      catalog: {
        ...productMetadata.catalog,
        fields,
      },
    },
  }));
  const { CaseRow } = await import("../../src/views/worklist/CaseRow");
  const value = {
    ...caseSummaryInput(),
    state: {
      kind: "ACTIVE" as const,
      fields: { ...listedFieldsInput, incidentDate: "2026-11-04" },
    },
  };
  render(
    <table>
      <tbody>
        <CaseRow value={value} onSelect={vi.fn()} />
      </tbody>
    </table>,
  );
  expect(screen.getByText("Incident date")).toBeVisible();
  expect(screen.getByText("04/11/2026")).toBeVisible();
});
