import { caseViewInput } from "../../../support/inputs";
import { beforeEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { definition, fields } from "../../../support/service-ui.fixtures";
import { caseAdvice } from "../../../support/case-advice.fixtures";
import { CaseFacts } from "../../../../src/views/case/CaseFacts";
import { CaseActions } from "../../../../src/views/case/CaseActions";
import { CaseProgress } from "../../../../src/views/case/CaseProgress";
import type { CurrentCase } from "../../../../src/api/service";
const compact = vi.hoisted(() => ({ value: false }));
vi.mock("../../../../src/hooks/useCompact", () => ({ useCompact: () => compact.value }));
beforeEach(() => {
  compact.value = false;
});
const current: CurrentCase = caseViewInput({
  caseId: "20000000-0000-4000-8000-000000000001",
  caseReference: fields.caseReference,
  revision: 2,
  state: { kind: "ACTIVE", fields },
  advice: { ...caseAdvice(["CLOSE", "VOID", "ERASE"]), history: [] },
});
it("renders grouped facts with missing decision and payment and retains help and exact copying", async () => {
  const user = userEvent.setup();
  const view = render(
    <CaseFacts values={fields} fields={definition.catalog.fields} revision={2} />,
  );
  expect(screen.getAllByText("Not recorded")).toHaveLength(2);
  expect(view.container.querySelectorAll(".case-fact-card")).toHaveLength(3);
  await user.click(screen.getAllByRole("button", { name: "Explain fields" })[1]!);
  expect(screen.getAllByRole("button", { name: /Copy/u }).length).toBeGreaterThan(1);
  expect(view.container.querySelector('[data-field-name="claimantName"]')).toHaveTextContent(
    fields.registration.claimantName,
  );
});
it("marks exactly one current progress step", () => {
  const view = render(<CaseProgress current={current} definition={definition.catalog} />);
  expect(view.container.querySelectorAll('[aria-current="step"]')).toHaveLength(1);
  expect(view.container.querySelector('[aria-current="step"]')).toHaveTextContent("Undecided");
});
it("separates stewardship and dispatches only server-advised acts with the first primary", async () => {
  const user = userEvent.setup();
  const onAct = vi.fn();
  const view = render(<CaseActions current={current} onAct={onAct} />);
  expect(view.container.querySelector(".case-stewardship")).toHaveTextContent("Void");
  expect(view.container.querySelector("button.primary")).toHaveTextContent("Close");
  await user.click(screen.getByRole("button", { name: /Erase case payload/u }));
  expect(onAct).toHaveBeenCalledWith("ERASE");
  expect(screen.queryByRole("button", { name: /Reinstate/u })).toBeNull();
});

it("keeps compact grouped facts visible with one explanation toggle per group", async () => {
  compact.value = true;
  const user = userEvent.setup();
  const view = render(
    <CaseFacts values={fields} fields={definition.catalog.fields} revision={2} />,
  );
  const cards = view.container.querySelectorAll("section.case-fact-card");
  expect(cards).toHaveLength(3);
  const toggle = cards[0]!.querySelector("button")!;
  expect(toggle).toHaveAttribute("aria-pressed", "false");
  await user.click(toggle);
  expect(toggle).toHaveAttribute("aria-pressed", "true");
  expect(view.container.querySelectorAll(".case-fact-card button.explain-fields")).toHaveLength(3);
});
it("keeps the first compact action available and reveals the remaining advised acts", async () => {
  compact.value = true;
  const user = userEvent.setup();
  const onAct = vi.fn();
  const view = render(<CaseActions current={current} onAct={onAct} />);
  expect(view.container.querySelector(".case-primary-action")).toHaveTextContent("Close");
  await user.click(view.container.querySelector(".case-more-actions > summary")!);
  await user.click(screen.getByRole("button", { name: /Erase case payload/u }));
  expect(onAct).toHaveBeenCalledWith("ERASE");
});
