import {
  caseDocumentInput,
  registrationInputs,
  emptyCaseSeeds,
} from "../support/case-document.fixtures";

import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { editor } from "../support/presentation-state.fixtures";

import { fields } from "../support/service-ui.fixtures";
const current = caseDocumentInput({
  tasks: ["case.amendRegistration"],
  caseId: "20000000-0000-4000-8000-000000000001",
  revision: 1,
  history: "FULL",
  editableInputs: [...registrationInputs],
  seeds: { ...emptyCaseSeeds, registration: fields.registration },
});
import { service } from "../../src/api/service";
import { changeField, selectFact } from "../support/field-entry";
const path = "field:incidentDate";
const group = () => screen.getByRole("group", { name: "Incident date" });
const segment = (type: string) => group().querySelector<HTMLElement>(`[data-type="${type}"]`)!;
const canonical = () => document.querySelector<HTMLInputElement>(`input[name="${path}"]`)!.value;
const enter = async (user: ReturnType<typeof userEvent.setup>, type: string, value: string) => {
  await user.click(segment(type));
  await user.keyboard(value);
};
const settings = async (user: ReturnType<typeof userEvent.setup>, format?: string) => {
  await user.click(document.querySelector<HTMLButtonElement>("[data-open-settings]")!);
  await screen.findByRole("heading", { name: "Settings" });
  if (format !== undefined) {
    const picker = screen.getByRole("combobox", { name: "Date and number format" });
    await user.click(picker);
    const option = document.querySelector<HTMLElement>(`[role="option"][data-key="${format}"]`)!;
    await user.click(option);
  }
  await user.click(document.querySelector<HTMLButtonElement>("[data-return-workspace]")!);
  await screen.findByRole("dialog");
};
it.each([
  { parts: [["day", "12"]] },
  { parts: [["month", "10"]] },
  {
    parts: [
      ["day", "12"],
      ["month", "10"],
    ],
  },
])(
  "keeps partial public date segments through Settings without inventing a canonical date: %j",
  async ({ parts }) => {
    const preview = vi.spyOn(service, "preview");
    const execute = vi.spyOn(service, "execute");
    const user = userEvent.setup();
    render(editor({ current: null, initialAct: "case.open" }));
    for (const [type, value] of parts) {
      await enter(user, type!, value!);
    }
    expect(canonical()).toBe("");
    await settings(user);
    for (const [type, value] of parts) {
      expect(segment(type!)).toHaveAttribute("aria-valuenow", value);
    }
    expect(canonical()).toBe("");
    expect(preview).not.toHaveBeenCalled();
    expect(execute).not.toHaveBeenCalled();
  },
);
it("keeps authored day/month meaning when the display locale changes order", async () => {
  const user = userEvent.setup();
  render(editor({ current: null, initialAct: "case.open" }));
  await enter(user, "day", "12");
  await enter(user, "month", "10");
  await settings(user, "en-US");
  expect(
    [...group().querySelectorAll('[role="spinbutton"]')].map((node) =>
      node.getAttribute("data-type"),
    ),
  ).toEqual(["month", "day", "year"]);
  expect(segment("day")).toHaveAttribute("aria-valuenow", "12");
  expect(segment("month")).toHaveAttribute("aria-valuenow", "10");
  expect(canonical()).toBe("");
});
it("protects a partial-only draft and clears its segments on explicit discard", async () => {
  const close = vi.fn();
  const user = userEvent.setup();
  render(editor({ current: null, initialAct: "case.open", onClose: close }));
  await enter(user, "day", "12");
  await user.click(screen.getAllByRole("button", { name: "Close" })[0]!);
  expect(await screen.findByRole("dialog", { name: "Discard these draft changes?" })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Keep editing" }));
  await waitFor(() => {
    expect(segment("day")).toHaveAttribute("aria-valuenow", "12");
  });
  await user.click(screen.getAllByRole("button", { name: "Close" })[0]!);
  await user.click(screen.getByRole("button", { name: "Discard and leave" }));
  expect(close).toHaveBeenCalledOnce();
  await waitFor(() => {
    expect(segment("day")).not.toHaveAttribute("aria-valuenow");
  });
});
it("empties a stale canonical date atomically when its visible year is cleared and restores the remaining parts", async () => {
  const user = userEvent.setup();
  render(
    editor({
      current,
      initialAct: "case.amendRegistration",
    }),
  );
  selectFact("incidentDate");
  changeField("Incident date", "2024-02-29");
  expect(document.querySelector<HTMLInputElement>('input[name="field:incidentDate"]')).toHaveValue(
    "2024-02-29",
  );
  await user.click(segment("year"));
  await user.keyboard("{Delete}{Delete}{Delete}{Delete}");
  await waitFor(() => {
    expect(
      document.querySelector<HTMLInputElement>('input[name="field:incidentDate"]'),
    ).toHaveValue("");
  });
  await settings(user);
  expect(segment("day")).toHaveAttribute("aria-valuenow", "29");
  expect(segment("month")).toHaveAttribute("aria-valuenow", "2");
  expect(segment("year")).not.toHaveAttribute("aria-valuenow");
  await enter(user, "year", "2024");
  await waitFor(() => {
    expect(
      document.querySelector<HTMLInputElement>('input[name="field:incidentDate"]'),
    ).toHaveValue("2024-02-29");
  });
});

it("keeps a complete leap date canonical and emits only its ISO value after Arabic formatting", async () => {
  const preview = vi.spyOn(service, "preview").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
    dispatched: false,
  });
  const execute = vi.spyOn(service, "execute");
  const user = userEvent.setup();
  render(editor({ current: null, initialAct: "case.open" }));
  await enter(user, "year", "2024");
  await enter(user, "month", "2");
  await enter(user, "day", "29");
  expect(canonical()).toBe("2024-02-29");
  await settings(user, "ar-EG");
  expect(segment("day")).toHaveAttribute("aria-valuenow", "29");
  expect(segment("day")).toHaveTextContent("٢٩");
  expect(canonical()).toBe("2024-02-29");
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(preview).toHaveBeenCalledOnce();
  });
  expect(preview.mock.calls[0]![0].input).toMatchObject({
    kind: "CASE",
    act: { kind: "case.open", values: { incidentDate: "2024-02-29" } },
  });
  expect(execute).not.toHaveBeenCalled();
});
it("an unrelated name edit preserves partial dates while deselection clears only its own field", async () => {
  const user = userEvent.setup();
  render(
    editor({
      current,
      initialAct: "case.amendRegistration",
    }),
  );
  selectFact("incidentDate");
  selectFact("incidentNotificationDate");
  selectFact("claimantName");
  changeField("Incident date", "");
  changeField("Notification date", "");
  await enter(user, "day", "12");
  const notified = screen.getByRole("group", { name: "Notification date" });
  await user.click(notified.querySelector<HTMLElement>('[data-type="month"]')!);
  await user.keyboard("10");
  await user.type(screen.getByRole("textbox", { name: "Claimant name" }), " edited");
  await user.click(screen.getByRole("checkbox", { name: "Change Incident date" }));
  await settings(user);
  const retained = screen.getByRole("group", { name: "Notification date" });
  expect(retained.querySelector('[data-type="month"]')).toHaveAttribute("aria-valuenow", "10");
  await user.click(screen.getByRole("checkbox", { name: "Change Incident date" }));
  expect(segment("day")).toHaveAttribute("aria-valuenow", "1");
  expect(document.querySelector('input[name="field:incidentDate"]')).toHaveValue(
    fields.registration.incidentDate,
  );
});
