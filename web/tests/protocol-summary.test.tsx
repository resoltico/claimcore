import { expect, it } from "vitest";
import { render, screen } from "./presentation-test-support";
import { ProtocolSummary } from "../src/components/ProtocolSummary";
import { PageStatus } from "../src/components/PageStatus";
import { wireSchemas } from "../src/protocol/client";
import type { SchemaDocument } from "../src/protocol/schema";
it("localizes generated enum tokens while preserving identical authored principal strings", () => {
  const document = wireSchemas["authority.execute"].request;
  render(
    <ProtocolSummary
      document={document}
      node={document}
      value={{
        operationId: "10000000-0000-4000-8000-000000000001",
        target: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "HUMAN" },
        act: { kind: "REGISTER_ACTOR" },
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
