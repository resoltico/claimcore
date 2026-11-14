import { Actions } from "../../ui/regions/Actions";
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
}: {
  target: PeopleTarget;
  busy: boolean;
  onOpen: () => void;
}) => (
  <fieldset disabled={busy}>
    <Actions
      actions={target.actions}
      expandOther
      onAction={(action) => {
        if (action.task !== "people.requests.dismiss") {
          target.begin(action);
          onOpen();
        }
      }}
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
  const retained = model.dirty || model.prepared !== null || model.priorUncertain;
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
            : offered.filter(
                (kind) => model.entry.kind !== "people.admit" || kind === "people.admit",
              )
        }
        disclosureAvailable={available}
        open={open}
        onClose={onClose}
      />
    </>
  );
};
