import { Dashboard } from "../../src/views/Dashboard";
import { actPath } from "../../src/routing/routes";
import { navigate } from "../../src/routing/navigation";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { chooseOption } from "../support/choice-entry";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { OperationEditor } from "../../src/views/OperationEditor";
import { SettingsPanel } from "../../src/features/settings/SettingsPanel";
import { preferenceKey } from "../../src/presentation/preferences";
import { productMetadata } from "../../src/protocol/productMetadata";
import { fields } from "../support/service-ui.fixtures";
import { currentCaseInput } from "../support/inputs";
import { caseAdvice } from "../support/case-advice.fixtures";
const current = currentCaseInput({
  caseId: "20000000-0000-4000-8000-000000000001",
  caseReference: fields.caseReference,
  revision: 4,
  state: { kind: "ACTIVE", fields },
  advice: { ...caseAdvice(["AMEND_REGISTRATION", "CORRECT", "CLOSE"]), history: "FULL" },
});
import { service } from "../../src/api/service";
import { changeField } from "../support/field-entry";

afterEach(() => vi.restoreAllMocks());
const preview = () =>
  vi.spyOn(service, "preview").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
  });
const editor = () =>
  render(
    <>
      <SettingsPanel />
      <OperationEditor
        definition={productMetadata}
        current={current}
        initialAct="AMEND_REGISTRATION"
        onClose={vi.fn()}
        onCommitted={vi.fn()}
        onMutationLockChange={vi.fn()}
      />
    </>,
  );
const name = async () => {
  const user = userEvent.setup();
  await user.click(screen.getByRole("checkbox", { name: "Change Claimant name" }));
  changeField("Claimant name", "Only selected claimant");
};
const prepared = async (spy: ReturnType<typeof preview>) => {
  await userEvent.setup().click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(spy).toHaveBeenCalledTimes(1);
  });
  return spy.mock.calls[0]![0];
};

it("sends only a selected registration fact and does not serialize retained context", async () => {
  const spy = preview();
  editor();
  await name();
  const frame = await prepared(spy);
  expect(frame.input).toMatchObject({ kind: "CASE", expectedRevision: 4 });
  expect(frame.input.act).toEqual({
    kind: "AMEND_REGISTRATION",
    values: { claimantName: "Only selected claimant" },
  });
});

it("selects and removes a complete money pair as one authoring event", async () => {
  const spy = preview();
  editor();
  await name();
  const pair = screen.getByRole("checkbox", {
    name: /^Change Claimed amount and Claimed currency/u,
  });
  await userEvent.setup().click(pair);
  changeField("Claimed amount", "50.25");
  expect(
    screen
      .getAllByRole("combobox", { name: "Claimed currency" })
      .some((control) => control.getAttribute("aria-hidden") !== "true"),
  ).toBe(true);
  await userEvent.setup().click(pair);
  expect(screen.queryByRole("textbox", { name: "Claimed amount" })).toBeNull();
  const frame = await prepared(spy);
  expect(frame.input.act).toEqual({
    kind: "AMEND_REGISTRATION",
    values: { claimantName: "Only selected claimant" },
  });
});

it.each([
  ["en-GB", "lv-LV", "1,234", "1234"],
  ["lv-LV", "en-GB", "1,234", "1.234"],
] as const)(
  "retains %s entry meaning through a real editor preference switch to %s",
  async (entered, display, raw, expected) => {
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({
        version: 3,
        language: "en",
        displayLocale: entered,
        mode: "system",
        accent: "green" as const,
      }),
    );
    const spy = preview();
    stubDashboardContext();
    const read = mockEndpoint("case.get").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        outcome: { kind: "SUCCEEDED", document: current },
      },
    });
    const execute = vi.spyOn(service, "execute");
    const path = actPath({ kind: "ID", caseId: current.context.caseId }, "AMEND_REGISTRATION");
    navigate(path);
    render(<Dashboard />);
    await screen.findByRole("checkbox", { name: /^Change Claimed amount and Claimed currency/u });
    await userEvent
      .setup()
      .click(
        screen.getByRole("checkbox", { name: /^Change Claimed amount and Claimed currency/u }),
      );
    changeField("Claimed amount", raw);
    const switchFormat = async (locale: string) => {
      const user = userEvent.setup();
      await user.click(screen.getByRole("button", { name: "Settings" }));
      await screen.findByRole("heading", { name: "Settings", level: 1 });
      expect(screen.queryByRole("dialog")).toBeNull();
      expect(window.location.pathname).toBe("/settings");
      await chooseOption(
        user,
        screen.getByRole("combobox", { name: "Date and number format" }),
        locale,
      );
      await user.click(screen.getByRole("button", { name: "Return to workspace" }));
      await screen.findByRole("dialog");
      expect(window.location.pathname).toBe(path);
    };
    await switchFormat(display);
    expect(screen.getByRole("textbox", { name: "Claimed amount" })).toHaveValue(raw);
    const frame = await prepared(spy);
    expect(frame.input.act).toEqual({
      kind: "AMEND_REGISTRATION",
      values: { claimedAmount: expected, claimedCurrency: "EUR" },
    });
    const { operationId: reviewedId, ...reviewedInput } = frame.input;
    await switchFormat(entered);
    await userEvent.setup().click(screen.getByRole("button", { name: "Review changes" }));
    await waitFor(() => {
      expect(spy).toHaveBeenCalledTimes(2);
    });
    const { operationId: nextId, ...nextInput } = spy.mock.calls[1]![0].input;
    expect(nextInput).toEqual(reviewedInput);
    expect(nextId).not.toBe(reviewedId);
    expect(read).toHaveBeenCalledOnce();
    expect(execute).not.toHaveBeenCalled();
  },
);
