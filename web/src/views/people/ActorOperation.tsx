import type { Notice } from "../../protocol/notices";
import { BrowserSessionCommands } from "../../features/settings/BrowserSessions";
import { isOperationAffordance } from "../../operation/affordances";
import { TaskControls } from "../../ui/TaskControls";
import { usePresentation } from "../../presentation/context";
import type { OperationModel } from "../../hooks/operation/useOperation";
import type { PeopleTarget } from "../../hooks/operation/usePeopleTarget";
import { OperationPanel } from "../operation/OperationPanel";
type Props = {
  model: OperationModel;
  target: PeopleTarget;
  offered: ReadonlyArray<string>;
  available: boolean;
  open: boolean;
  onOpen: () => void;
  onClose: () => void;
};
export const ActorActions = ({
  target,
  busy,
  onOpen,
  onDisclosureRefused,
}: {
  target: PeopleTarget;
  busy: boolean;
  onOpen: () => void;
  onDisclosureRefused?: ((notice?: Notice) => void) | undefined;
}) => (
  <fieldset disabled={busy}>
    <TaskControls
      compact
      actions={target.actions.filter(isOperationAffordance)}
      onAction={(action) => {
        if (isOperationAffordance(action)) {
          target.begin(action);
          onOpen();
        }
      }}
    />
    <BrowserSessionCommands
      actions={target.actions}
      disabled={busy}
      onDisclosureRefused={onDisclosureRefused}
    />
  </fieldset>
);
export const ActorOperation = ({
  model,
  target,
  offered,
  available,
  open,
  onOpen,
  onClose,
}: Props) => {
  const p = usePresentation();
  const retained = model.protectsIntent;
  return (
    <>
      {!open && retained ? (
        <>
          <p role="status">{p.text("ui.actorRequestRetained")}</p>
          <button type="button" onClick={onOpen}>
            {p.text("ui.continuePeopleRequest")}
          </button>
        </>
      ) : null}
      <OperationPanel
        model={model}
        {...(target.mode === "bound" && target.person !== null ? { person: target.person } : {})}
        current={null}
        offered={
          target.mode === "bound"
            ? target.offered
            : offered.filter((kind) => !model.task.creates || kind === model.entry.kind)
        }
        disclosureAvailable={available && target.selectionCurrent}
        open={open}
        onClose={onClose}
      />
    </>
  );
};
