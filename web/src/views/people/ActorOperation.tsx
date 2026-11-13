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
}) => {
  const p = usePresentation();
  if (target.offered.length === 0) {
    return null;
  }
  const groups = [
    {
      label: p.text("ui.people.roles"),
      acts: target.offered.filter((kind) => kind === "people.grant" || kind === "people.revoke"),
    },
    {
      label: p.text("ui.account"),
      acts: target.offered.filter((kind) => kind !== "people.grant" && kind !== "people.revoke"),
    },
  ];
  return (
    <>
      {groups
        .filter((group) => group.acts.length > 0)
        .map((group) => (
          <section key={group.label} aria-label={group.label}>
            <h2>{group.label}</h2>
            <div className="actions">
              {group.acts.map((kind) => (
                <button
                  key={kind}
                  className={kind === target.primary ? "primary" : undefined}
                  type="button"
                  disabled={busy}
                  aria-label={p.text("ui.actDescription", {
                    label: p.peopleLabel(kind),
                    meaning: p.peopleMeaning(kind),
                  })}
                  onClick={() => {
                    target.begin(kind);
                    onOpen();
                  }}
                >
                  {p.peopleLabel(kind)}
                </button>
              ))}
            </div>
          </section>
        ))}
    </>
  );
};
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
