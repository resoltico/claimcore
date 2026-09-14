import type { CommandDraft } from "../api/v2";

type FlatCommand = Exclude<CommandDraft["command"], { readonly kind: "CORRECT_CASE" }>;
type CorrectionGroup =
  | { readonly mode: "KEEP" }
  | { readonly mode: "CLEAR" }
  | { readonly mode: "REPLACE"; readonly values: object };

const cloneFlat = <T extends FlatCommand>(command: T): T => ({
  ...command,
  values: { ...command.values },
});

const cloneGroup = <T extends CorrectionGroup>(group: T): T =>
  group.mode === "REPLACE" ? { ...group, values: { ...group.values } } : { ...group };

export const freezeRequest = (draft: CommandDraft): CommandDraft => {
  if (draft.command.kind !== "CORRECT_CASE") return { ...draft, command: cloneFlat(draft.command) };
  return {
    ...draft,
    command: {
      ...draft.command,
      groups: {
        registration: cloneGroup(draft.command.groups.registration),
        decision: cloneGroup(draft.command.groups.decision),
        payment: cloneGroup(draft.command.groups.payment),
      },
    },
  };
};
