import type { CurrentCase } from "../../api/service";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { usePresentation } from "../../presentation/context";
import { operationEntry } from "../../protocol/operationCatalog";
import { Icon } from "../../ui/icons";
import type { ActKind } from "../../operation/metadata";
import type { FocusEvent, KeyboardEvent } from "react";

const closeOnBlur = (event: FocusEvent<HTMLElement>) => {
  const details = event.currentTarget.closest("details");
  if (
    details !== null &&
    (!(event.relatedTarget instanceof Node) || !details.contains(event.relatedTarget))
  ) {
    details.open = false;
  }
};

const closeOnEscape = (event: KeyboardEvent<HTMLElement>) => {
  const details = event.currentTarget.closest("details");
  if (event.key === "Escape" && details?.open === true) {
    event.preventDefault();
    event.stopPropagation();
    details.open = false;
    details.querySelector("summary")?.focus();
  }
};

export const CaseSectionActions = ({
  current,
  section,
  onAct,
}: {
  current: CurrentCase;
  section: "registration" | "decision" | "payment";
  onAct: (act: ActKind) => void;
}) => {
  const p = usePresentation();
  const requester = useRequesterContext();
  const acts = current.advice.acts.filter((act) => {
    const definition = operationEntry({ subject: "CASE", kind: act });
    return definition.effect === "RECORD_FACTS" && definition.affectedSections.includes(section);
  });
  if (
    current.advice.maintenance.active ||
    requester?.maintenance.active === true ||
    acts.length === 0
  ) {
    return null;
  }
  const [only] = acts;
  if (acts.length === 1 && only !== undefined) {
    return (
      <button
        type="button"
        className="case-edit-link"
        aria-label={p.actLabel(only)}
        title={p.actLabel(only)}
        onClick={() => {
          onAct(only);
        }}
      >
        <Icon name="edit" />
      </button>
    );
  }
  const label = p.text("ui.changeFact", { field: p.text(`group.${section}.label`) });
  return (
    <details className="case-section-actions">
      <summary
        className="case-edit-link"
        aria-label={label}
        title={label}
        onKeyDown={closeOnEscape}
        onBlur={closeOnBlur}
      >
        <Icon name="edit" />
      </summary>
      <div className="case-section-actions-popup">
        {acts.map((act) => (
          <button
            type="button"
            key={act}
            onKeyDown={closeOnEscape}
            onBlur={closeOnBlur}
            onClick={(event) => {
              const details = event.currentTarget.closest("details");
              if (details !== null) {
                details.querySelector("summary")?.focus();
                details.open = false;
              }
              onAct(act);
            }}
          >
            {p.actLabel(act)}
          </button>
        ))}
      </div>
    </details>
  );
};
