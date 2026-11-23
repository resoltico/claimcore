import { useId } from "react";
import { SuppliedMessage } from "../../ui/SuppliedMessage";
import { PhoneAction } from "../../ui/PhoneAction";
import { Icon } from "../../ui/icons";
import { taskDefinitions, type Affordance } from "@contracts/client";
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
  phonePrimary = false,
  hideRecommendation = false,
}: {
  current: CurrentCase;
  phonePrimary?: boolean;
  hideRecommendation?: boolean;
  onAct: (action: Affordance) => void;
}) => {
  const p = usePresentation();
  const blockedId = useId();
  const recommendation = current.screen.regions
    .filter(({ role }) => role === "RECOMMENDATION")
    .flatMap(({ actions }) => actions ?? [])
    .find((action) => isCaseAct(action.task));
  const alternatives = current.actions.flatMap((action) =>
    isCaseAct(action.task)
      ? [
          {
            id: action.task,
            label: renderCatalogKey(p, action.labelId),
            consequence: operationTask(action.task).consequence,
            disabled: action.blocked !== null,
            ...(action.blocked === null
              ? {}
              : {
                  description: (
                    <SuppliedMessage
                      message={action.blocked.message}
                      timeZone={current.basis.observation.timeZone}
                    />
                  ),
                }),
            select: () => {
              onAct(action);
            },
          },
        ]
      : [],
  );
  const primary = (compact: boolean) =>
    recommendation === undefined ? null : (
      <div>
        <Button
          data-act={recommendation.task}
          aria-label={renderCatalogKey(
            p,
            (compact ? recommendation.shortLabelId : undefined) ?? recommendation.labelId,
          )}
          {...(recommendation.blocked === null ? {} : { "aria-describedby": blockedId })}
          compact={compact}
          isDisabled={recommendation.blocked !== null}
          onPress={() => {
            onAct(recommendation);
          }}
        >
          {compact &&
          taskDefinitions.find((task) => task.id === recommendation.task)?.creates === true ? (
            <Icon name="add" />
          ) : (
            renderCatalogKey(
              p,
              (compact ? recommendation.shortLabelId : undefined) ?? recommendation.labelId,
            )
          )}
        </Button>
        {recommendation.blocked === null ? null : (
          <span id={blockedId} className="field-hint">
            <SuppliedMessage
              message={recommendation.blocked.message}
              timeZone={current.basis.observation.timeZone}
            />
          </span>
        )}
      </div>
    );
  const primaryAction = phonePrimary ? (
    <PhoneAction renderCompact={() => primary(true)}>{primary(false)}</PhoneAction>
  ) : (
    primary(false)
  );
  return (
    <>
      {hideRecommendation ? null : primaryAction}
      {alternatives.length === 0 ? null : (
        <ActionMenu
          label={p.text("ui.moreCaseActions")}
          stewardshipLabel={p.text("label.dataStewardship")}
          items={alternatives}
          variant="secondary"
          trigger={
            <>
              {p.text("ui.moreCaseActions")} <Icon name="chevron-down" />
            </>
          }
        />
      )}
    </>
  );
};
