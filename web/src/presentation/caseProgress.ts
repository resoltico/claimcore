import type { CaseFields } from "@contracts/client";
/** Present retained decision/payment facts; the server remains authoritative for offered acts. */
export const caseProgress = (fields: CaseFields): "UNDECIDED" | "DECIDED" | "PAID" => {
  if (fields.payment !== null) {
    return "PAID";
  }
  if (fields.decision !== null) {
    return "DECIDED";
  }
  return "UNDECIDED";
};
