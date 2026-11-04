import { usePresentation } from "../../presentation/context";
import type { OperationModel } from "../../hooks/operation/useOperation";
import type { AuthorityTarget } from "../../hooks/operation/useAuthorityTarget";
import { OperationPanel } from "../operation/OperationPanel";
type Props = {
  model: OperationModel;
  target: AuthorityTarget;
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
  target: AuthorityTarget;
  busy: boolean;
  onOpen: () => void;
}) => {
  const p = usePresentation();
  if (target.offered.length === 0) {
    return null;
  }
  return (
    <section aria-labelledby="actor-actions-title">
      <h2 id="actor-actions-title">{p.text("label.availableActs")}</h2>
      <div className="actions">
        {target.offered.map((kind) => (
          <button
            key={kind}
            className={kind === target.primary ? "primary" : undefined}
            type="button"
            disabled={busy}
            aria-label={p.text("ui.actDescription", {
              label: p.authorityLabel(kind),
              meaning: p.authorityMeaning(kind),
            })}
            onClick={() => {
              target.begin(kind);
              onOpen();
            }}
          >
            {p.authorityLabel(kind)}
          </button>
        ))}
      </div>
    </section>
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
            {p.text("ui.continueAuthorityRequest")}
          </button>
        </>
      ) : null}
      <OperationPanel
        model={model}
        {...(target.mode === "bound" && target.actor !== null ? { actor: target.actor.actor } : {})}
        current={null}
        offered={
          target.mode === "bound"
            ? target.offered
            : offered.filter((kind) => model.entry.kind !== "ADMIT" || kind === "ADMIT")
        }
        fixedTarget={target.mode === "bound"}
        fixedPaths={model.entry.kind === "ADMIT" ? ["target.issuer"] : []}
        disclosureAvailable={available}
        open={open}
        onClose={onClose}
      />
    </>
  );
};
