import userEvent from "@testing-library/user-event";
import * as navigation from "../../../../src/routing/navigation";
import { expect, it, vi } from "vitest";
import { render, screen } from "../../../support/presentation-test-support";
import { RegionView } from "../../../../src/ui/regions/RegionView";
it("renders the supplied nonlinear tracker without deriving completion, dates or amounts", () => {
  const view = render(
    <RegionView
      region={{
        role: "PROGRESS",
        content: {
          kind: "STAGES",
          current: "CLOSED",
          steps: [
            {
              stage: "OPENED",
              reached: true,
              date: "2026-10-31",
              amount: { amount: "100", currency: "USD", displayScale: 2 },
            },
            { stage: "DECIDED", reached: false, date: null, amount: null },
            { stage: "PAID", reached: false, date: null, amount: null },
            { stage: "CLOSED", reached: true, date: "2026-11-04", amount: null },
          ],
        },
      }}
    />,
  );
  expect(view.container.querySelectorAll("li")).toHaveLength(4);
  expect(screen.getByText("Closed", { exact: true }).closest("li")).toHaveAttribute(
    "aria-current",
    "step",
  );
  expect(view.container.querySelectorAll("li[data-complete=true]")).toHaveLength(2);
  expect(view.container).toHaveTextContent("31/10/2026");
  expect(view.container).toHaveTextContent("04/11/2026");
  expect(view.container).toHaveTextContent("100.00 USD");
  expect(view.container.querySelector("li:nth-child(2)")).toHaveTextContent("Not recorded");
  expect(view.container.querySelector("li:nth-child(3)")).toHaveTextContent("Not recorded");
});
it("preserves the complete numeric date and exact amount display scale", () => {
  const view = render(
    <RegionView
      region={{
        role: "PROGRESS",
        content: {
          kind: "STAGES",
          current: "OPENED",
          steps: [
            {
              stage: "OPENED",
              reached: true,
              date: "0001-01-01",
              amount: { amount: "123456789012345678.0100", currency: "EUR", displayScale: 4 },
            },
            { stage: "DECIDED", reached: false, date: null, amount: null },
            { stage: "PAID", reached: false, date: null, amount: null },
            { stage: "CLOSED", reached: false, date: null, amount: null },
          ],
        },
      }}
    />,
  );
  const time = view.container.querySelector("time")!;
  expect(time).toHaveAttribute("datetime", "0001-01-01");
  expect(time.textContent).toBe("01/01/0001");
  expect(view.container).toHaveTextContent("123,456,789,012,345,678.0100 EUR");
});

it("renders supplied stage controls and follows its supplied query link", async () => {
  const action = {
    task: "case.close",
    binding: {
      kind: "CASE",
      target: { kind: "REFERENCE", caseReference: "CASE-1" },
      expectedRevision: 4,
    },
    seeds: {},
    editable: [],
    choices: {},
    primary: true,
    blocked: null,
  } as const;
  const onAction = vi.fn();
  const follow = vi.spyOn(navigation, "navigate").mockImplementation(() => false);
  render(
    <RegionView
      region={{
        role: "PROGRESS",
        content: {
          kind: "STAGES",
          current: "OPENED",
          steps: [
            {
              stage: "OPENED",
              reached: true,
              date: null,
              amount: null,
              link: {
                endpoint: "case.get",
                input: { target: { kind: "REFERENCE", caseReference: "CASE-1" } },
              },
              actions: [action],
            },
          ],
        },
      }}
      onAction={onAction}
    />,
  );
  const button = screen.getByRole("button", { name: "Close the case" });
  expect(button.classList.contains("primary")).toBe(true);
  await userEvent.setup().click(button);
  expect(onAction).toHaveBeenCalledExactlyOnceWith(action, undefined, expect.any(String));
  const link = screen.getByRole("link", { name: "Opened" });
  expect(link).toHaveAttribute("href", "/cases/reference/CASE-1");
  await userEvent.setup().click(link);
  expect(follow).toHaveBeenCalledExactlyOnceWith("/cases/reference/CASE-1");
  follow.mockRestore();
});

it("keeps a supplied blocked stage action disabled", async () => {
  const onAction = vi.fn();
  render(
    <RegionView
      region={{
        role: "PROGRESS",
        content: {
          kind: "STAGES",
          current: "OPENED",
          steps: [
            {
              stage: "OPENED",
              reached: true,
              date: null,
              amount: null,
              actions: [
                {
                  task: "case.close",
                  binding: {
                    kind: "CASE",
                    target: { kind: "REFERENCE", caseReference: "CASE-1" },
                    expectedRevision: 4,
                  },
                  seeds: {},
                  editable: [],
                  choices: {},
                  primary: false,
                  blocked: {
                    action: "ASK_FOR_ACCESS",
                    message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
                  },
                },
              ],
            },
          ],
        },
      }}
      onAction={onAction}
    />,
  );
  const button = screen.getByRole("button");
  expect(button).toBeDisabled();
  await userEvent.setup().click(button);
  expect(onAction).not.toHaveBeenCalled();
});
