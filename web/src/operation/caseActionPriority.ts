import type { CurrentCase } from "../protocol/types";
import { isCaseAct, type ActKind } from "./metadata";

/** Client task ranking is explicit and independent of vocabulary or offer declaration order. */
export const casePrimaryAction = (current: CurrentCase): ActKind | undefined => {
  if (current.state.kind !== "ACTIVE" || current.state.fields.status !== "OPENED") {
    return undefined;
  }
  const offered = current.advice.acts.filter(isCaseAct);
  const preferred = {
    UNDECIDED: "DECIDE",
    DECIDED: "RECORD_PAYMENT",
    PAID: "CLOSE",
  } as const;
  const primary = preferred[current.state.fields.progress];
  if (offered.includes(primary)) {
    return primary;
  }
  return (["UPDATE_WORK", "AMEND_REGISTRATION", "CORRECT"] as const).find((act) =>
    offered.includes(act),
  );
};
