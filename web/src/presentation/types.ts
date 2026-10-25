import type { MessageArgs, MessageKey } from "@contracts/messages";
export type { MessageArgs, MessageKey } from "@contracts/messages";
export type Values<K extends MessageKey> =
  MessageArgs[K] extends Readonly<Record<string, never>>
    ? [args?: MessageArgs[K]]
    : [args: MessageArgs[K]];
