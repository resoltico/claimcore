import type { RegionContent } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { ButtonControl } from "../ButtonControl";
import type { OfferedAct } from "./interaction";
import "./actions.css";
export const Actions = ({
  content,
  onAct,
  expandOther = false,
}: {
  content: Extract<RegionContent, { kind: "ACTIONS" }>;
  onAct?: (act: OfferedAct) => void;
  expandOther?: boolean;
}) => {
  const p = usePresentation();
  const button = (act: OfferedAct, primary = false) =>
    onAct === undefined ? (
      <span key={act}>{p.actLabel(act)}</span>
    ) : (
      <ButtonControl
        key={act}
        className={primary ? "primary" : ""}
        data-act={act}
        aria-label={p.text("ui.actDescription", {
          label: p.actLabel(act),
          meaning: p.actMeaning(act),
        })}
        onPress={() => {
          onAct(act);
        }}
      >
        {p.actLabel(act)}
      </ButtonControl>
    );
  const groups = content.groups.map((group) => (
    <section key={group.family} data-family={group.family}>
      <h3>{p.term("actFamily", group.family)}</h3>
      <div className="actions">{group.acts.map((act) => button(act))}</div>
    </section>
  ));
  let other = null;
  if (content.groups.length > 0) {
    other = expandOther ? (
      <div className="case-action-alternatives">{groups}</div>
    ) : (
      <details className="case-actions">
        <summary>{p.text("ui.moreCaseActions")}</summary>
        <div className="case-actions-popup">{groups}</div>
      </details>
    );
  }
  return (
    <div className="region-actions">
      <h2 className="sr-only">{p.text("label.availableActs")}</h2>
      {content.recommended === null ? null : button(content.recommended, true)}
      {other}
    </div>
  );
};
