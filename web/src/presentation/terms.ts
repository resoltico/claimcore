import { termKeys } from "@contracts/messages";
import type { Terms, MessageKey } from "@contracts/messages";
import { renderKey } from "./messages";
import type { Preferences } from "./preferences";

const labels: { readonly [V in keyof Terms]: Readonly<Record<Terms[V], MessageKey>> } = termKeys;
export const isTerm = <V extends keyof Terms>(vocabulary: V, value: string): value is Terms[V] =>
  Object.hasOwn(labels[vocabulary], value);
export const termLabel = <V extends keyof Terms>(
  preferences: Preferences,
  vocabulary: V,
  value: Terms[V],
): string => renderKey(preferences, labels[vocabulary][value]);
