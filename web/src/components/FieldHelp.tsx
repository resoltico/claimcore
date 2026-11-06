import "./field-help.css";
import { Icon } from "../ui/icons";
import { usePresentation } from "../presentation/context";

/** Precise core rules are available on request; ordinary field descriptions contain hints only. */
export const FieldHelp = ({ label, ruleId }: { label: string; ruleId: string | null }) => {
  const p = usePresentation();
  return ruleId === null ? null : (
    <details className="field-help">
      <summary
        aria-label={`${p.text("ui.explainFields")}: ${label}`}
        onBlur={(event) => {
          const details = event.currentTarget.closest("details");
          if (
            details !== null &&
            (!(event.relatedTarget instanceof Node) || !details.contains(event.relatedTarget))
          ) {
            details.open = false;
          }
        }}
        onKeyDown={(event) => {
          const details = event.currentTarget.closest("details");
          if (event.key === "Escape" && details?.open === true) {
            event.preventDefault();
            event.stopPropagation();
            details.open = false;
            event.currentTarget.focus();
          }
        }}
      >
        <Icon name="info" />
      </summary>
      <p>{p.rule(ruleId)}</p>
    </details>
  );
};
