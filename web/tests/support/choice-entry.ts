import type userEvent from "@testing-library/user-event";
/** Exercise radio and shared listbox choices by their wire value. */
export const chooseOption = async (
  user: ReturnType<typeof userEvent.setup>,
  target: HTMLElement,
  value: string,
): Promise<void> => {
  if (target.getAttribute("role") === "combobox") {
    if (target.getAttribute("aria-expanded") !== "true") {
      await user.click(target);
    }
    const popup = document.getElementById(target.getAttribute("aria-controls") ?? "");
    const option = [...(popup?.querySelectorAll<HTMLElement>('[role="option"]') ?? [])].find(
      (item) => item.getAttribute("data-key") === value,
    );
    if (option === undefined) {
      throw new Error(`No listbox choice ${value}.`);
    }
    await user.click(option);
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

/** Values are inspected only after the user opens the actual popup. */
export const optionValues = (target: HTMLElement): string[] => {
  const popup = document.getElementById(target.getAttribute("aria-controls") ?? "");
  return [...(popup?.querySelectorAll<HTMLElement>('[role="option"]') ?? [])]
    .map((item) => item.getAttribute("data-key"))
    .filter((value) => value !== null);
};
