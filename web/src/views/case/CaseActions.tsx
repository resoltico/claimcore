import type { Affordance } from "@contracts/client";
import type { CurrentCase } from "../../api/service";
import { isCaseAct } from "../../operation/metadata";
import { operationTask } from "../../protocol/taskCatalog";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { ActionMenu } from "../../ui/ActionMenu";
import { Button } from "../../ui/Button";

export const CaseActions = ({
  current,
  onAct,
}: {
  current: CurrentCase;
  onAct: (action: Affordance) => void;
}) => {
  const p = usePresentation();
  const recommendation = current.screen.regions
    .filter(({ role }) => role === "RECOMMENDATION")
    .flatMap(({ actions }) => actions ?? [])
    .find((action) => isCaseAct(action.task) && action.blocked === null);
  const alternatives = current.actions.flatMap((action) =>
    isCaseAct(action.task)
      ? [
          {
            id: action.task,
            label: renderCatalogKey(p, action.labelId),
            consequence: operationTask(action.task).consequence,
            disabled: action.blocked !== null,
            select: () => {
              onAct(action);
            },
          },
        ]
      : [],
  );
  return (
    <>
      {recommendation === undefined ? null : (
        <Button
          data-act={recommendation.task}
          onPress={() => {
            onAct(recommendation);
          }}
        >
          {renderCatalogKey(p, recommendation.labelId)}
        </Button>
      )}
      {alternatives.length === 0 ? null : (
        <ActionMenu
          label={p.text("ui.moreCaseActions")}
          stewardshipLabel={p.text("label.dataStewardship")}
          items={alternatives}
        />
      )}
    </>
  );
};
