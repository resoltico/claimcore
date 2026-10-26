import type {
  MessageArgs as CoreMessageArgs,
  MessageKey as CoreMessageKey,
} from "@contracts/messages";
import type {
  MessageArgs as ChromeMessageArgs,
  MessageKey as ChromeMessageKey,
} from "../generated/chrome-messages";
export type MessageArgs = CoreMessageArgs & ChromeMessageArgs;
export type MessageKey = CoreMessageKey | ChromeMessageKey;
export type Values<K extends MessageKey> =
  MessageArgs[K] extends Readonly<Record<string, never>>
    ? [args?: MessageArgs[K]]
    : [args: MessageArgs[K]];
