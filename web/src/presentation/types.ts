import type {
  MessageArgs as ContractMessageArgs,
  MessageKey as ContractMessageKey,
} from "@contracts/messages";
import type { ChromeMessageKey } from "./chromeMessages";
export type MessageKey = Exclude<ContractMessageKey, `ui.${string}`> | ChromeMessageKey;
export type MessageArgs = { [K in MessageKey]: ContractMessageArgs[K & keyof ContractMessageArgs] };
export type Values<K extends MessageKey> =
  MessageArgs[K] extends Readonly<Record<string, never>>
    ? [args?: MessageArgs[K]]
    : [args: MessageArgs[K]];
