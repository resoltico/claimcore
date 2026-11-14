import type { CurrentCase } from "../../api/service";
import { isCaseAct, type ActKind } from "../../operation/metadata";
import { Actions } from "../../ui/regions/Actions";
const caseActions = (current: CurrentCase) =>
  current.actions.filter((action) => isCaseAct(action.task));
export const CaseActions = ({
  current,
  onAct,
}: {
  current: CurrentCase;
  onAct: (act: ActKind) => void;
}) => (
  <Actions
    actions={caseActions(current)}
    onAction={(action) => {
      if (isCaseAct(action.task)) {
        onAct(action.task);
      }
    }}
  />
);
export const CaseActionAlternatives = ({
  current,
  onAct,
}: {
  current: CurrentCase;
  onAct: (act: ActKind) => void;
}) => (
  <Actions
    actions={caseActions(current)}
    expandOther
    onAction={(action) => {
      if (isCaseAct(action.task)) {
        onAct(action.task);
      }
    }}
  />
);
