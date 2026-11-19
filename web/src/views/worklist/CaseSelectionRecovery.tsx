import { useEffect, useId, useRef, useState } from "react";
import { usePresentation } from "../../presentation/context";
import { isTerm } from "../../presentation/terms";
import { navigate } from "../../routing/navigation";
import {
  repairWorklistQuery,
  type SelectionIssue,
  type WorklistAdmission,
} from "../../routing/worklistQuery";

type InvalidSelection = Extract<WorklistAdmission, { kind: "invalid" }>;

const SelectionRepair = ({
  admission,
  issue,
}: {
  admission: InvalidSelection;
  issue: SelectionIssue;
}) => {
  const p = usePresentation();
  const id = useId();
  const field = issue.key;
  const label =
    field !== undefined && isTerm("caseDateFilter", field)
      ? p.term("caseDateFilter", field)
      : (field ?? "");
  const [values, setValues] = useState(() =>
    issue.fragments.map((fragment) => ({
      position: fragment.position,
      value: fragment.value ?? fragment.raw.slice(fragment.raw.indexOf("=") + 1),
    })),
  );
  const submit = (next: ReadonlyArray<string>) => {
    navigate(repairWorklistQuery(admission.fragments, issue, next));
  };
  return (
    <form
      className="case-selection-repair"
      onSubmit={(event) => {
        event.preventDefault();
        submit(values.map(({ value }) => value));
      }}
    >
      <p id={id}>
        {issue.upper === undefined
          ? p.text("ui.caseSelectionRepairField")
          : p.text("diagnostic.CASE_DATE_RANGE", { field: label })}
      </p>
      {field === undefined ? (
        <ul>
          {issue.fragments.map((fragment) => (
            <li key={fragment.position}>
              <bdi>{fragment.raw}</bdi>
            </li>
          ))}
        </ul>
      ) : (
        <div className="case-selection-values">
          {values.map(({ position, value }) => (
            <label key={position}>
              <bdi>{label}</bdi>
              <input
                dir="auto"
                aria-invalid="true"
                aria-describedby={id}
                value={value}
                onChange={(event) => {
                  const next = event.currentTarget.value;
                  setValues((previous) =>
                    previous.map((item) =>
                      item.position === position ? { position, value: next } : item,
                    ),
                  );
                }}
              />
            </label>
          ))}
        </div>
      )}
      <div className="worklist-filters">
        {field === undefined ? null : (
          <button type="submit">{p.text("ui.caseSelectionApply")}</button>
        )}
        <button
          type="button"
          onClick={() => {
            submit([]);
          }}
        >
          {p.text("ui.caseSelectionClear")}
        </button>
      </div>
    </form>
  );
};

export const CaseSelectionRecovery = ({ admission }: { admission: InvalidSelection }) => {
  const p = usePresentation();
  const title = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    title.current?.focus();
  }, []);
  return (
    <section aria-labelledby="case-list-title" className="worklist">
      <h1 id="case-list-title" tabIndex={-1} ref={title}>
        {p.text("label.cases")}
      </h1>
      <p role="alert">{p.text("ui.caseSelectionInvalid")}</p>
      {admission.issues.map((issue) => (
        <SelectionRepair key={issue.fragments[0]?.position} admission={admission} issue={issue} />
      ))}
    </section>
  );
};
