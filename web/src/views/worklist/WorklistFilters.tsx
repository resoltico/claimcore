import { Button } from "../../ui/Button";
import { TextInput, ChoiceControl } from "../../ui/TextField";
import { apiEndpoints } from "../../api/service";
import { Select } from "../../ui/Select";
import { Dialog } from "../../ui/Dialog";
import { useState, type ReactNode } from "react";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { isTerm, isVocabulary } from "../../presentation/terms";
import { queryInput } from "../../protocol/taskCatalog";
import {
  invalidCaseDateEnd,
  reportDateFields,
  type WorklistQuery,
} from "../../routing/worklistQuery";
type Props = { query: WorklistQuery; onChange: (query: WorklistQuery) => void };
const selectSet = (
  query: WorklistQuery,
  name: string,
  values: ReadonlyArray<string>,
): WorklistQuery => {
  const manual = query;
  switch (name) {
    case "lifecycle":
      return { ...manual, lifecycle: values.filter((value) => isTerm("lifecycle", value)) };
    case "status":
      return { ...manual, status: values.filter((value) => isTerm("status", value)) };
    case "progress":
      return { ...manual, progress: values.filter((value) => isTerm("progress", value)) };
    default:
      return manual;
  }
};
const inputs = (
  ["input:lifecycle", "input:status", "input:progress", "input:assignment", "input:due"] as const
).map((id) => queryInput(apiEndpoints.caseList, id));
const selectedValues = (query: WorklistQuery, name: string): ReadonlyArray<string> => {
  switch (name) {
    case "lifecycle":
      return query.lifecycle ?? [];
    case "status":
      return query.status ?? [];
    case "progress":
      return query.progress ?? [];
    default:
      return [];
  }
};
const FilterSet = ({ query, onChange, input }: Props & { input: (typeof inputs)[number] }) => {
  const p = usePresentation();
  if (!["input:lifecycle", "input:status", "input:progress"].includes(input.id)) {
    return null;
  }
  const { vocabulary, choices: options } = input;
  const values = selectedValues(query, input.key);
  const label = renderCatalogKey(p, input.labelId);
  return (
    <fieldset>
      <legend>{label}</legend>
      {options.map((option) => (
        <label key={option}>
          <ChoiceControl
            type="checkbox"
            checked={values.includes(option)}
            onChange={(event) => {
              onChange(
                selectSet(
                  query,
                  input.key,
                  event.target.checked
                    ? [...values, option]
                    : values.filter((value) => value !== option),
                ),
              );
            }}
          />
          {vocabulary !== null && isVocabulary(vocabulary) && isTerm(vocabulary, option)
            ? p.term(vocabulary, option)
            : option}
        </label>
      ))}
    </fieldset>
  );
};
const WorkChoice = ({ query, onChange, input }: Props & { input: (typeof inputs)[number] }) => {
  const p = usePresentation();
  if (input.key !== "assignment" && input.key !== "due") {
    return null;
  }
  const selected = input.key === "assignment" ? query.assignment : query.due;
  return (
    <Select
      inlineLabel
      label={renderCatalogKey(p, input.labelId)}
      value={selected ?? ""}
      options={[
        { value: "", label: p.text("ui.anyFilter") },
        ...input.choices.map((option) => ({
          value: option,
          label:
            input.vocabulary !== null &&
            isVocabulary(input.vocabulary) &&
            isTerm(input.vocabulary, option)
              ? p.term(input.vocabulary, option)
              : option,
        })),
      ]}
      onChange={(value) => {
        if (input.key === "assignment") {
          const { assignment: _assignment, ...rest } = query;
          onChange(isTerm("assignment", value) ? { ...rest, assignment: value } : rest);
        } else {
          const { due: _due, ...rest } = query;
          onChange(isTerm("due", value) ? { ...rest, due: value } : rest);
        }
      }}
    />
  );
};
const OrderControl = ({ query, onChange }: Props) => {
  const p = usePresentation();
  const options = queryInput(apiEndpoints.caseList, "input:order").choices;
  return (
    <Select
      inlineLabel
      label={p.text("input.order.label")}
      value={query.order ?? "REFERENCE"}
      options={options
        .filter((order) => isTerm("caseOrder", order))
        .map((order) => ({ value: order, label: p.term("caseOrder", order) }))}
      onChange={(value) => {
        const manual = query;
        onChange({ ...manual, order: isTerm("caseOrder", value) ? value : "REFERENCE" });
      }}
    />
  );
};
const ReportDates = ({
  query,
  onChange,
  invalid,
}: Props & {
  invalid: ReturnType<typeof invalidCaseDateEnd>;
}) => {
  const p = usePresentation();
  return (
    <fieldset className="report-date-fields">
      <legend>{p.text("ui.reportingDates")}</legend>
      {reportDateFields.map((field) => {
        const input = queryInput(apiEndpoints.caseList, `input:${field}`);
        return (
          <div key={field}>
            <label>
              {renderCatalogKey(p, input.labelId)}
              <TextInput
                type="date"
                value={query[field] ?? ""}
                aria-invalid={invalid === field || undefined}
                aria-describedby={invalid === field ? "report-date-range-error" : undefined}
                onChange={(event) => {
                  const { [field]: _previous, ...manual } = query;
                  const { value } = event.target;
                  onChange(value === "" ? manual : { ...manual, [field]: value });
                }}
              />
            </label>
            {invalid === field ? (
              <span id="report-date-range-error" role="alert">
                {p.text("diagnostic.CASE_DATE_RANGE", {
                  field: renderCatalogKey(p, input.labelId),
                })}
              </span>
            ) : null}
          </div>
        );
      })}
    </fieldset>
  );
};
const advancedKeys = ["lifecycle", "status", "progress", ...reportDateFields] as const;

export const WorklistFilters = ({
  query,
  onChange,
  exportAction,
}: Props & { exportAction?: ReactNode }) => {
  const p = usePresentation();
  const [draft, setDraft] = useState<WorklistQuery | null>(null);
  const [invalid, setInvalid] = useState<ReturnType<typeof invalidCaseDateEnd>>();
  const active = advancedKeys.reduce((count, key) => {
    const value = query[key];
    if (Array.isArray(value)) {
      return count + value.length;
    }
    return value === undefined ? count : count + 1;
  }, 0);
  const changeDraft = (next: WorklistQuery) => {
    setInvalid(undefined);
    setDraft(next);
  };
  const replaceAdvanced = (next: WorklistQuery): WorklistQuery => ({
    ...Object.fromEntries(
      Object.entries(query).filter(([key]) => !advancedKeys.some((candidate) => candidate === key)),
    ),
    ...Object.fromEntries(
      Object.entries(next).filter(([key]) => advancedKeys.some((candidate) => candidate === key)),
    ),
  });
  return (
    <div className="worklist-filter-controls">
      {inputs
        .filter(({ key }) => key === "due" || key === "assignment")
        .map((input) => (
          <WorkChoice key={input.key} input={input} query={query} onChange={onChange} />
        ))}
      <OrderControl query={query} onChange={onChange} />
      <div className="toolbar-actions">
        <Button
          variant="secondary"
          aria-haspopup="dialog"
          onPress={() => {
            changeDraft(query);
          }}
        >
          {active === 0
            ? p.text("ui.filters")
            : p.text("ui.filtersWithCount", { count: p.integer(active) })}
        </Button>
        {exportAction}
      </div>
      <Dialog
        title={p.text("ui.filters")}
        closeLabel={p.text("ui.close")}
        className="filter-sheet"
        isOpen={draft !== null}
        isDismissable
        onOpenChange={(open) => {
          if (!open) {
            setDraft(null);
          }
        }}
      >
        {draft === null ? null : (
          <form
            onSubmit={(event) => {
              event.preventDefault();
              const end = invalidCaseDateEnd(draft);
              if (end !== undefined) {
                setInvalid(end);
                return;
              }
              onChange(replaceAdvanced(draft));
              setDraft(null);
            }}
          >
            <div className="worklist-filters">
              {inputs
                .filter(({ key }) => key !== "due" && key !== "assignment")
                .map((input) => (
                  <FilterSet key={input.key} input={input} query={draft} onChange={changeDraft} />
                ))}
            </div>
            <ReportDates query={draft} onChange={changeDraft} invalid={invalid} />
            <div className="worklist-actions">
              <button
                type="button"
                onClick={() => {
                  changeDraft({});
                }}
              >
                {p.text("ui.clearFilters")}
              </button>
              <button
                type="button"
                onClick={() => {
                  setDraft(null);
                }}
              >
                {p.text("ui.cancelChanges")}
              </button>
              <button type="submit">{p.text("ui.applyFilters")}</button>
            </div>
          </form>
        )}
      </Dialog>
    </div>
  );
};
