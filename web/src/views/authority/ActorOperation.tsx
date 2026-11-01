import { CharacterWarning } from "../../components/BusinessValue";
import { navigate } from "../../routing/navigation";
import { usePresentation } from "../../presentation/context";
import type { OperationModel } from "../../hooks/operation/useOperation";
import type { AuthorityTarget } from "../../hooks/operation/useAuthorityTarget";
import { OperationPanel } from "../operation/OperationPanel";
type Props = {
  model: OperationModel;
  target: AuthorityTarget;
  offered: ReadonlyArray<string>;
  available: boolean;
};
const ActorActions = ({ target, busy }: { target: AuthorityTarget; busy: boolean }) => {
  const p = usePresentation();
  if (target.offered.length === 0) {
    return null;
  }
  return (
    <section aria-labelledby="actor-actions-title">
      <h2 id="actor-actions-title">
        {p.text("label.availableActs")}
        {target.actor === null ? null : (
          <>
            {" "}
            · <bdi dir="auto">{target.actor.principal.subject}</bdi>
            <CharacterWarning value={target.actor.principal.subject} />
          </>
        )}
      </h2>
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
            }}
          >
            {p.authorityLabel(kind)}
          </button>
        ))}
      </div>
    </section>
  );
};
const RetainedActorOperation = ({ model, target, available, offered }: Props) => {
  const p = usePresentation();
  return (
    <>
      <p role="status">{p.text("ui.actorRequestRetained")}</p>
      <button
        type="button"
        onClick={() => {
          navigate("/authority");
        }}
      >
        {p.text("ui.continueAuthorityRequest")}
      </button>
      <OperationPanel
        model={model}
        current={null}
        offered={offered}
        disclosureAvailable={available}
      />
      <ActorActions target={target} busy={model.busy} />
    </>
  );
};

export const ActorOperation = ({ model, target, offered, available }: Props) => {
  const p = usePresentation();
  if (!available || target.mode === "list" || target.mode === "bound") {
    return (
      <>
        {available && target.mode === "bound" ? (
          <h2>{p.authorityLabel(model.entry.kind)}</h2>
        ) : null}
        <OperationPanel
          model={model}
          current={null}
          offered={
            target.mode === "bound" ? offered.filter((kind) => kind !== "REGISTER") : offered
          }
          fixedTarget={target.mode === "bound"}
          disclosureAvailable={available}
        />
      </>
    );
  }
  if (target.mode === "unbound") {
    return null;
  }
  return (
    <RetainedActorOperation model={model} target={target} available={available} offered={offered} />
  );
};
