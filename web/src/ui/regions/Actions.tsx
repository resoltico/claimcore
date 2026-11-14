import type { ActionPresentation } from "./interaction";
import { useId } from "react";
import { type Affordance, taskDefinitions } from "@contracts/client";

import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { ButtonControl } from "../ButtonControl";
import { SuppliedMessage } from "../SuppliedMessage";
import {} from "./actions.css";
/** Controls arrive with their exact Core binding and recommendation. */
export const Actions = ({
  actions,
  onAction,
  actionPresentation,
  compact = false,
  disabled = false,
  expandOther = false,
}: {
  actions: ReadonlyArray<Affordance>;
  actionPresentation?: ActionPresentation;
  onAction?: (action: Affordance, originId?: string) => void;
  compact?: boolean;
  disabled?: boolean;
  expandOther?: boolean;
}) => {
  const p = usePresentation();
  const id = useId();
  const button = (action: Affordance) => {
    const task = taskDefinitions.find((definition) => definition.id === action.task)!;
    const display = actionPresentation?.(action);
    const label = display?.label ?? renderCatalogKey(p, task.labelId);
    const originId = display?.id ?? `${id}-${action.task}`;
    return (
      <span key={action.task}>
        {onAction === undefined ? (
          <span>{label}</span>
        ) : (
          <ButtonControl
            id={originId}
            className={action.primary ? "primary" : ""}
            data-act={action.task}
            isDisabled={disabled || action.blocked !== null}
            aria-label={
              compact
                ? label
                : p.text("ui.actDescription", {
                    label,
                    meaning: renderCatalogKey(p, task.meaningId),
                  })
            }
            onPress={() => {
              onAction(action, originId);
            }}
          >
            {label}
          </ButtonControl>
        )}
        {action.blocked === null ? null : (
          <span className="sr-only">
            <SuppliedMessage message={action.blocked.message} timeZone="UTC" />
          </span>
        )}
      </span>
    );
  };
  const primary = actions.find((action) => action.primary);
  const other = actions.filter((action) => !action.primary);
  if (compact) {
    return <div className="actions">{actions.map(button)}</div>;
  }
  const alternatives = expandOther ? (
    <div className="case-action-alternatives">{other.map(button)}</div>
  ) : (
    <details className="case-actions">
      <summary>{p.text("ui.moreCaseActions")}</summary>
      <div className="case-actions-popup">
        <div className="actions">{other.map(button)}</div>
      </div>
    </details>
  );
  return (
    <div className="region-actions">
      <h2 className="sr-only">{p.text("label.availableActs")}</h2>
      {primary === undefined ? null : button(primary)}
      {other.length === 0 ? null : alternatives}
    </div>
  );
};
