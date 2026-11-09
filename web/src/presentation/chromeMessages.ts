import english from "../../locales/en.json" with { type: "json" };
import { templateParts } from "./templates";

export type ChromeMessageKey = keyof typeof english;
export const chromeShapes = Object.fromEntries(
  Object.entries(english).map(([key, text]) => [
    key,
    Object.fromEntries(
      templateParts(text)
        .filter((part) => part.kind === "hole")
        .map((part) => [part.value, "string"]),
    ),
  ]),
);
export const bootMessages = {
  loading: english["ui.loading"],
  retry: english["ui.tryAgain"],
  reload: english["ui.reloadInterface"],
};
