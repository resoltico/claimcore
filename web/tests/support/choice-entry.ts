import type userEvent from "@testing-library/user-event";
/** Exercise native radio choices and the remaining native select controls by their wire value. */
export const chooseOption = async (
  user: ReturnType<typeof userEvent.setup>,
  target: HTMLElement,
  value: string,
): Promise<void> => {
  if (target instanceof HTMLSelectElement) {
    await user.selectOptions(target, value);
    return;
  }
  const group = target.matches('[role="radiogroup"]')
    ? target
    : target.closest('[role="radiogroup"]');
  const option = [
    ...(group ?? target).querySelectorAll<HTMLInputElement>('input[type="radio"]'),
  ].find((input) => input.value === value);
  if (option === undefined) {
    throw new Error(`No radio choice ${value}.`);
  }
  await user.click(option);
};
export const selectedOption = (target: HTMLElement): string | null =>
  target.querySelector<HTMLInputElement>("input:checked")?.value ?? null;
