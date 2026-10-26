import { CaseHistory } from "../src/views/CaseHistory";
import { service } from "../src/api/service";
import { productMetadata } from "../src/protocol/productMetadata";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { ProtocolSummary } from "../src/components/ProtocolSummary";
import { PageStatus } from "../src/components/PageStatus";
import { selectedSchema } from "../src/protocol/forms";
import { wireSchemas } from "../src/protocol/documents";
import type { SchemaDocument } from "../src/protocol/schema";
it("localizes generated enum tokens while preserving identical authored principal strings", () => {
  const document = wireSchemas["operation.execute"].request;
  render(
    <ProtocolSummary
      document={document}
      node={selectedSchema(document, document, "", { scope: "AUTHORITY" })}
      value={{
        operationId: "10000000-0000-4000-8000-000000000001",
        target: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "HUMAN" },
        act: { kind: "REGISTER" },
      }}
    />,
  );
  expect(screen.getByText("Human", { exact: true })).toBeVisible();
  expect(screen.getAllByText("HUMAN", { exact: true })).toHaveLength(2);
  expect(screen.getByText("Register actor", { exact: true })).toBeVisible();
});
it("uses schema membership rather than field spelling and preserves whitespace as authored input", () => {
  const document: SchemaDocument = {
    type: "object",
    properties: {
      kind: { type: "string" },
      subject: { type: "string" },
      issuer: { type: "string" },
    },
  };
  const { container } = render(
    <ProtocolSummary
      document={document}
      node={document}
      value={{ kind: "HUMAN", subject: " ", issuer: "" }}
    />,
  );
  expect(screen.queryByText("Human", { exact: true })).toBeNull();
  expect(screen.getByText("HUMAN", { exact: true })).toBeVisible();
  expect(screen.getAllByText("Empty input", { exact: true })).toHaveLength(1);
  expect([...container.querySelectorAll("bdi")].some((node) => node.textContent === " ")).toBe(
    true,
  );
});
it("an empty short nonterminal page never claims the end of visible scope", () => {
  const { rerender } = render(
    <PageStatus empty cursor="synthetic-continuation" emptyMessage="ui.noAccessibleCases" />,
  );
  expect(screen.getByText("No accessible cases returned.")).toBeVisible();
  expect(screen.getByText("More results are available in this visible scope.")).toBeVisible();
  expect(screen.queryByText("End of results in this visible scope.")).toBeNull();
  rerender(<PageStatus empty cursor={null} emptyMessage="ui.noAccessibleCases" />);
  expect(screen.getByText("End of results in this visible scope.")).toBeVisible();
});

afterEach(() => {
  vi.restoreAllMocks();
});
it("a failed history read stops loading without inventing a successful empty page", async () => {
  vi.spyOn(service, "history").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
    dispatched: false,
  });
  render(
    <CaseHistory
      token="synthetic"
      caseReference="SYNTHETIC"
      fields={productMetadata.catalog.fields}
    />,
  );
  expect(await screen.findByRole("alert")).toBeVisible();
  expect(screen.queryByRole("status")).toBeNull();
  expect(screen.queryByText("No accessible history entries returned.")).toBeNull();
  expect(screen.queryByText("End of results in this visible scope.")).toBeNull();
});
