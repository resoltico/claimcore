import { screen, waitFor } from "./presentation-test-support";

/** The single action inside the current review, independent of its localized catalog label. */
export const queryReviewConfirm = (): HTMLButtonElement | null =>
  screen
    .queryByRole("dialog")
    ?.querySelector<HTMLButtonElement>("button[data-confirm-operation]") ?? null;
export const reviewConfirm = (): HTMLButtonElement => {
  const button = queryReviewConfirm();
  if (button === null) {
    throw new Error("Expected one current reviewed-action button.");
  }
  return button;
};
export const waitForReviewConfirm = () => waitFor(reviewConfirm);
