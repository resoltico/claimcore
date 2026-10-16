import type { CommandDraft } from "../api/service";

export const freezeRequest = (draft: CommandDraft): CommandDraft => structuredClone(draft);
