import rendered from "../../tests/fixtures/rendered-messages.json" with { type: "json" };
import type { catalogs, MessageKey } from "../../contracts/messages";

// Expected labels come from frozen F# renderings, independent of the browser formatter.
export const inputMessage = (language: keyof typeof catalogs, key: MessageKey): string => {
  const row = rendered.find((entry) => entry.language === language && entry.key === key);
  if (row === undefined || Object.keys(row.arguments).length !== 0) {
    throw new Error("An argument-free frozen message is required.");
  }
  return row.expected;
};
