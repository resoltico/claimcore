import type { Affordance } from "@contracts/client";
import type { CurrentCase } from "../../api/service";
import { isCaseAct } from "../../operation/metadata";
import { Actions } from "../../ui/regions/Actions";
const caseActions = (current: CurrentCase) =>
  current.actions.filter((action) => isCaseAct(action.task));
export const CaseActions = ({
  current,
  onAct,
}: {
  current: CurrentCase;
  onAct: (action: Affordance) => void;
}) => (
  <>
    <Actions
      actions={current.screen.regions
        .filter((region) => region.role === "RECOMMENDATION")
        .flatMap((region) => region.actions ?? [])
        .filter((action) => isCaseAct(action.task))}
      prominent
      onAction={(action) => {
        if (isCaseAct(action.task)) {
          onAct(action);
        }
      }}
    />
    <Actions
      actions={caseActions(current)}
      onAction={(action) => {
        if (isCaseAct(action.task)) {
          onAct(action);
        }
      }}
    />
  </>
);
export const CaseActionAlternatives = ({
  current,
  onAct,
}: {
  current: CurrentCase;
  onAct: (action: Affordance) => void;
}) => (
  <Actions
    actions={caseActions(current)}
    expandOther
    onAction={(action) => {
      if (isCaseAct(action.task)) {
        onAct(action);
      }
    }}
  />
);
