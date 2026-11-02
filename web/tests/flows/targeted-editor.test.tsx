import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { OperationEditor } from "../../src/views/OperationEditor";
import { PresentationControls } from "../../src/presentation/PresentationControls";
import { preferenceKey } from "../../src/presentation/preferences";
import { productMetadata } from "../../src/protocol/productMetadata";
import { current } from "../support/operation-form.fixtures";
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
      <PresentationControls />
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
  const pair = screen.getByRole("checkbox", { name: /^Change Amount claimed and Currency/u });
  await userEvent.setup().click(pair);
  changeField("Amount claimed", "50.25");
  expect(screen.getByRole("combobox", { name: "Currency of claimed amount" })).toBeVisible();
  await userEvent.setup().click(pair);
  expect(screen.queryByRole("textbox", { name: "Amount claimed" })).toBeNull();
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
      JSON.stringify({ version: 2, language: "en", displayLocale: entered, theme: "SYSTEM" }),
    );
    const spy = preview();
    editor();
    await userEvent
      .setup()
      .click(screen.getByRole("checkbox", { name: /^Change Amount claimed and Currency/u }));
    changeField("Amount claimed", raw);
    await userEvent
      .setup()
      .selectOptions(screen.getByRole("combobox", { name: "Date and number format" }), display);
    expect(screen.getByRole("textbox", { name: "Amount claimed" })).toHaveValue(raw);
    const frame = await prepared(spy);
    expect(frame.input.act).toEqual({
      kind: "AMEND_REGISTRATION",
      values: { claimed: { amount: expected, currency: "EUR" } },
    });
    const { bytes } = frame;
    await userEvent
      .setup()
      .selectOptions(screen.getByRole("combobox", { name: "Date and number format" }), entered);
    await userEvent.setup().click(screen.getByRole("button", { name: "Review changes" }));
    await waitFor(() => {
      expect(spy).toHaveBeenCalledTimes(2);
    });
    expect(spy.mock.calls[1]![0].bytes).toBe(bytes);
  },
);
