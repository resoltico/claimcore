import { useLayoutEffect, useRef } from "react";
import { Button } from "react-aria-components/Button";
import type { CurrentCase } from "../../api/service";
import { isCaseAct, type ActKind } from "../../operation/metadata";
import { useCompact } from "../../hooks/useCompact";
import { usePresentation } from "../../presentation/context";
const stewardship = new Set<ActKind>(["VOID", "REINSTATE", "ERASE"]);
type Action = { act: ActKind; primary: ActKind | undefined; onAct: (act: ActKind) => void };
const ActButton = ({ act, primary, onAct }: Action) => {
  const p = usePresentation();
  let className = act === primary ? "primary" : "";
  if (act === "ERASE") {
    className = `${className} case-action-danger`.trim();
  }
  return (
    <Button
      onPress={() => {
        onAct(act);
      }}
      className={className}
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
      <div className="actions">{buttons(acts.filter((act) => !stewardship.has(act)))}</div>
      {acts.some((act) => stewardship.has(act)) ? (
        <section className="case-stewardship">
          <h3>{p.text("ui.caseStewardship")}</h3>
          <div className="actions">{buttons(acts.filter((act) => stewardship.has(act)))}</div>
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
      {acts.length < 2 ? null : (
        <details className="case-more-actions">
          <summary>
            {p.text("ui.moreCaseActions")} <span aria-hidden="true">⌄</span>
          </summary>
          <div className="case-more-popup">
            <ActionGroups acts={acts.slice(1)} primary={primary} onAct={onAct} />
          </div>
        </details>
      )}
    </aside>
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
  const acts = current.advice.acts.filter(isCaseAct);
  const [primary] = acts;
  if (acts.length === 0) {
    return null;
  }
  if (compact) {
    return <CompactActions acts={acts} primary={primary} onAct={onAct} />;
  }
  return (
    <aside className="case-actions" aria-labelledby="acts-title">
      <h2 id="acts-title">{p.text("label.availableActs")}</h2>
      <ActionGroups acts={acts} primary={primary} onAct={onAct} />
    </aside>
  );
};
