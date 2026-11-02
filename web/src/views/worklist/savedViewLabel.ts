import type { Presentation } from "../../presentation/context";
import type { SavedView } from "../../routing/worklistQuery";
const viewLabels = {
  mine: "ui.viewMine",
  "open-undecided": "ui.viewOpenUndecided",
  "decided-unpaid": "ui.viewDecidedUnpaid",

  all: "ui.viewAll",
} as const;
export const savedViewLabel = (p: Presentation, view: SavedView): string => {
  switch (view) {
    case "unassigned":
      return p.term("assignment", "UNASSIGNED");
    case "due-today":
      return p.term("due", "TODAY");
    case "overdue":
      return p.term("due", "OVERDUE");
    case "recent":
      return p.term("caseOrder", "RECENT");
    case "closed":
      return p.term("status", "CLOSED");
    case "voided":
      return p.term("lifecycle", "VOIDED");
    default:
      return p.text(viewLabels[view]);
  }
};
