import { useId } from "react";
import { type Affordance, taskDefinitions } from "@contracts/client";
import type { ActionPresentation } from "./regions/interaction";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import { ActionMenu } from "./ActionMenu";
import { Button } from "./Button";

export const TaskControls = ({
  actions,
  onAction,
  actionPresentation,
  compact = false,
  disabled = false,
  prominent = false,
}: {
  actions: ReadonlyArray<Affordance>;
  onAction?: (action: Affordance, originId?: string) => void;
  actionPresentation?: ActionPresentation;
  compact?: boolean;
  disabled?: boolean;
  prominent?: boolean;
}) => {
  const p = usePresentation();
  const id = useId();
  if (actions.length === 0) {
    return null;
  }
  const described = actions.map((action) => {
    const task = taskDefinitions.find(({ id: taskId }) => taskId === action.task)!;
    const label = actionPresentation?.(action).label ?? renderCatalogKey(p, action.labelId);
    return {
      id: action.task,
      label,
      description: p.text("ui.actDescription", {
        label,
        meaning: renderCatalogKey(p, task.meaningId),
      }),
      disabled: disabled || action.blocked !== null,
      consequence: task.consequence,
      select: () => {
        onAction?.(action, id);
      },
    };
  });
  if (onAction === undefined) {
    return (
      <ul>
        {described.map((item) => (
          <li key={item.id}>{item.label}</li>
        ))}
      </ul>
    );
  }
  if (prominent) {
    return (
      <div className="actions">
        {described.map((item) => (
          <Button
            key={item.id}
            data-act={item.id}
            aria-label={item.description}
            compact={compact}
            isDisabled={item.disabled}
            onPress={item.select}
          >
            {item.label}
          </Button>
        ))}
      </div>
    );
  }
  return (
    <ActionMenu
      id={id}
      label={p.text("ui.moreCaseActions")}
      items={described}
      stewardshipLabel={p.text("label.dataStewardship")}
    />
  );
};
