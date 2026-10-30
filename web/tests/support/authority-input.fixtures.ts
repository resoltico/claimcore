import { fireEvent } from "@testing-library/react";

/** Complete only an omitted role in positive interaction scenarios; explicit selections are preserved. */
export const chooseRoleForReview = (): void => {
  const group = document.querySelector('[role="radiogroup"][id="act.role"]');
  if (group === null || group.querySelector("input:checked") !== null) {
    return;
  }
  const role = group.querySelector<HTMLInputElement>('input[value="CASE_READER"]');
  if (role !== null && !role.disabled) {
    fireEvent.click(role);
  }
};
