import { useRequesterContext } from "../../hooks/useRequesterContext";
import { useLayoutEffect, useRef } from "react";
import { Button } from "react-aria-components/Button";
import type { CurrentCase } from "../../api/service";
import { isCaseAct, type ActKind } from "../../operation/metadata";
import { useCompact } from "../../hooks/useCompact";
import { usePresentation } from "../../presentation/context";
import { operationEntry } from "../../protocol/operationCatalog";
import { casePrimaryAction } from "../../operation/caseActionPriority";
const stewardship = (act: ActKind) =>
  operationEntry({ subject: "CASE", kind: act }).family === "STEWARDSHIP";
type Action = { act: ActKind; primary: ActKind | undefined; onAct: (act: ActKind) => void };
const ActButton = ({ act, primary, onAct }: Action) => {
  const p = usePresentation();
  let className = act === primary ? "primary" : "";
  if (operationEntry({ subject: "CASE", kind: act }).effect === "ERASE_PAYLOAD") {
    className = `${className} case-action-danger`.trim();
  }
  return (
    <Button
      onPress={() => {
        onAct(act);
      }}
      className={className}
      data-act={act}
      aria-label={p.text("ui.actDescription", {
        label: p.actLabel(act),
        meaning: p.actMeaning(act),
      })}
    >
      {p.actLabel(act)}
    </Button>
  );
};
type GroupProps = Omit<Action, "act"> & { acts: ReadonlyArray<ActKind> };
const ActionGroups = ({ acts, primary, onAct }: GroupProps) => {
  const p = usePresentation();
  const buttons = (items: ReadonlyArray<ActKind>) =>
    items.map((act) => <ActButton key={act} act={act} primary={primary} onAct={onAct} />);
  return (
    <>
      <div className="actions">{buttons(acts.filter((act) => !stewardship(act)))}</div>
      {acts.some((act) => stewardship(act)) ? (
        <section className="case-stewardship">
          <h3>{p.term("actFamily", "STEWARDSHIP")}</h3>
          <div className="actions">{buttons(acts.filter((act) => stewardship(act)))}</div>
        </section>
      ) : null}
    </>
  );
};
const useActionBar = () => {
  const ref = useRef<HTMLElement>(null);
  useLayoutEffect(() => {
    const bar = ref.current;
    const detail = bar?.closest<HTMLElement>(".case-detail");
    if (bar === null || detail === null || detail === undefined) {
      return;
    }
    const measure = () => {
      detail.style.setProperty("--case-action-bar-height", `${bar.offsetHeight}px`);
    };
    measure();
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(measure);
    observer?.observe(bar);
    return () => {
      observer?.disconnect();
      detail.style.removeProperty("--case-action-bar-height");
    };
  }, []);
  return ref;
};
const CompactActions = ({ acts, primary, onAct }: GroupProps) => {
  const p = usePresentation();
  const ref = useActionBar();
  return (
    <aside ref={ref} className="case-action-bar" aria-labelledby="acts-title">
      <h2 id="acts-title" className="sr-only">
        {p.text("label.availableActs")}
      </h2>
      <div className="case-primary-action">
        {primary === undefined ? null : <ActButton act={primary} primary={primary} onAct={onAct} />}
      </div>
      {acts.filter((act) => act !== primary).length === 0 ? null : (
        <details className="case-more-actions">
          <summary>
            {p.text("ui.moreCaseActions")} <span aria-hidden="true">⌄</span>
          </summary>
          <div className="case-more-popup">
            <ActionGroups
              acts={acts.filter((act) => act !== primary)}
              primary={primary}
              onAct={onAct}
            />
          </div>
        </details>
      )}
    </aside>
  );
};
export const CasePrimaryAction = ({
  current,
  onAct,
}: {
  current: CurrentCase;
  onAct: Action["onAct"];
}) => {
  const compact = useCompact();
  const requester = useRequesterContext();
  const paused = current.advice.maintenance.active || requester?.maintenance.active === true;
  const primary = casePrimaryAction(current);
  return compact || paused || primary === undefined ? null : (
    <ActButton act={primary} primary={primary} onAct={onAct} />
  );
};
export const CaseActions = ({
  current,
  onAct,
}: {
  current: CurrentCase;
  onAct: Action["onAct"];
}) => {
  const p = usePresentation();
  const compact = useCompact();
  const requester = useRequesterContext();
  const paused = current.advice.maintenance.active || requester?.maintenance.active === true;
  const acts = paused ? [] : current.advice.acts.filter(isCaseAct);
  const primary = paused ? undefined : casePrimaryAction(current);
  if (acts.length === 0) {
    return null;
  }
  if (compact) {
    return <CompactActions acts={acts} primary={primary} onAct={onAct} />;
  }
  if (acts.every((act) => act === primary)) {
    return null;
  }
  return (
    <aside className="case-actions" aria-labelledby="acts-title">
      <h2 id="acts-title">{p.text("ui.continueCase")}</h2>
      <ActionGroups acts={acts.filter((act) => act !== primary)} primary={primary} onAct={onAct} />
    </aside>
  );
};
