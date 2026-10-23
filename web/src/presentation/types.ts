import type { AuthorityArgs } from "./generated/args.authority";
import type { UiArgs } from "./generated/args.ui";
import type { NoticeArgs } from "./generated/args.notice";
import type { TokenArgs } from "./generated/args.token";
import type { FieldArgs } from "./generated/args.field";
import type { CommandArgs } from "./generated/args.command";
import type { GroupArgs } from "./generated/args.group";
import type { DiagnosticArgs } from "./generated/args.diagnostic";
import type { FaultArgs } from "./generated/args.fault";
import type { HostArgs } from "./generated/args.host";
import type { CliArgs } from "./generated/args.cli";
export type MessageArgs = FaultArgs &
  HostArgs &
  CliArgs &
  AuthorityArgs &
  UiArgs &
  NoticeArgs &
  TokenArgs &
  FieldArgs &
  CommandArgs &
  GroupArgs &
  DiagnosticArgs;
export type MessageKey = keyof MessageArgs;
export type Values<K extends MessageKey> =
  MessageArgs[K] extends Readonly<Record<string, never>>
    ? [args?: MessageArgs[K]]
    : [args: MessageArgs[K]];
