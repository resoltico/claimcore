import type { MessageArgs as CoreMessageArgs } from "@contracts/messages";
import type { MessageArgs as ChromeMessageArgs } from "../generated/chrome-messages";
export type MessageArgs = CoreMessageArgs & ChromeMessageArgs;
export type MessageKey = keyof MessageArgs;
export type Values<K extends MessageKey> =
  MessageArgs[K] extends Readonly<Record<string, never>>
    ? [args?: MessageArgs[K]]
    : [args: MessageArgs[K]];
