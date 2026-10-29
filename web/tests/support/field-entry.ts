import { fireEvent, screen } from "@testing-library/react";
const fieldControl = (target: HTMLElement | string): HTMLElement => {
  if (typeof target !== "string") {
    return target;
  }
  return (
    screen.queryByRole("group", { name: target }) ??
    screen.queryByRole("combobox", { name: target }) ??
    screen.getByLabelText(target, { exact: true })
  );
};
const changeChoice = (control: HTMLElement, value: string): boolean => {
  const group = control.matches('[role="radiogroup"]')
    ? control
    : control.closest('[role="radiogroup"]');
  if (group === null) {
    return false;
  }
  const option = [...group.querySelectorAll<HTMLInputElement>('input[type="radio"]')].find(
    (input) => input.value === value,
  );
  if (option === undefined) {
    throw new Error(`Missing radio choice ${value}.`);
  }
  fireEvent.click(option);
  return true;
};
const liveSegment = (control: HTMLElement, type: string): HTMLElement =>
  (document.getElementById(control.id) ?? control).querySelector<HTMLElement>(
    `[data-type=${type}]`,
  )!;
/** Exercise segmented dates through keyboard events and ordinary inputs through change events. */
export const changeField = (target: HTMLElement | string, value: string): void => {
  const control = fieldControl(target);
  if (changeChoice(control, value)) {
    return;
  }
  const segments = control.querySelectorAll<HTMLElement>("[role=spinbutton]");
  if (segments.length === 0) {
    fireEvent.change(control, { target: { value } });
    return;
  }
  const parts = /^(\d{4})-(\d{2})-(\d{2})$/u.exec(value);
  if (value !== "" && parts === null) {
    throw new Error("A segmented date fixture must supply ISO calendar parts or clear the value.");
  }
  for (const [type, digits] of [
    ["year", parts?.[1]],
    ["month", parts?.[2]],
    ["day", parts?.[3]],
  ] as const) {
    const segment = control.querySelector<HTMLElement>(`[data-type=${type}]`)!;
    segment.focus();
    if (value === "") {
      for (let index = 0; index < 4; index += 1) {
        fireEvent.keyDown(segment, { key: "Delete" });
      }
    }
    if (digits !== undefined) {
      const live = liveSegment(control, type);
      live.focus();
      fireEvent(
        live,
        new InputEvent("beforeinput", {
          data: digits,
          inputType: "insertText",
          bubbles: true,
          cancelable: true,
        }),
      );
    }
  }
};
