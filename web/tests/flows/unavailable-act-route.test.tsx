import { expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { current } from "../support/presentation-state.fixtures";
import { RoutedContent } from "../../src/views/RoutedContent";
import { service } from "../../src/api/service";
it("keeps a primary heading and a case return when a reloaded act is no longer offered", async () => {
  const closed = {
    ...current,
    advice: { ...current.advice, acts: ["REOPEN"] as const, history: [] },
    state: { ...current.state, fields: { ...current.state.fields, status: "CLOSED" as const } },
  };
  mockEndpoint("case.get").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { kind: "SUCCEEDED", data: closed } },
  });
  const preview = vi.spyOn(service, "preview");
  const execute = vi.spyOn(service, "execute");
  render(
    <RoutedContent
      route={{ screen: "caseAct", reference: current.caseReference, act: "CLOSE" }}
      setLocked={vi.fn()}
      committed={vi.fn()}
      refresh={0}
    />,
  );
  expect(await screen.findByRole("heading", { name: "Case detail", level: 1 })).toBeVisible();
  expect(screen.getByRole("button", { name: "Back to case" })).toBeVisible();
  expect(screen.queryByRole("button", { name: "Review changes" })).toBeNull();
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});
