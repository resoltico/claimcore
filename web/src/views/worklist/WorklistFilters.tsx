import { Select } from "../../ui/Select";
import { Dialog } from "../../ui/Dialog";
import { useState } from "react";
import { useCompact } from "../../hooks/useCompact";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { isTerm, isVocabulary } from "../../presentation/terms";
import { queryInput } from "../../protocol/taskCatalog";
import type { WorklistQuery } from "../../routing/worklistQuery";
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
).map((id) => queryInput("case.list", id));
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
    <details>
      <summary>
        {label}
        {values.length ? ` (${p.integer(values.length)})` : ""}
      </summary>
      <fieldset>
        <legend className="sr-only">{label}</legend>
        {options.map((option) => (
          <label key={option}>
            <input
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
    </details>
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
  const options = queryInput("case.list", "input:order").choices;
  return (
    <Select
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
const FilterControls = (props: Props) => (
  <div className="worklist-filters">
    {inputs.map((input) => (
      <span key={input.key}>
        <FilterSet input={input} {...props} />
        <WorkChoice input={input} {...props} />
      </span>
    ))}
    <OrderControl {...props} />
  </div>
);
export const WorklistFilters = (props: Props) => {
  const p = usePresentation();
  const compact = useCompact();
  const [open, setOpen] = useState(false);
  if (!compact) {
    return <FilterControls {...props} />;
  }
  return (
    <>
      <button
        type="button"
        aria-haspopup="dialog"
        aria-expanded={open}
        onClick={() => {
          setOpen(true);
        }}
      >
        {p.text("ui.filters")}
      </button>
      <Dialog
        title={p.text("ui.filters")}
        closeLabel={p.text("ui.close")}
        className="filter-sheet"
        placement="bottom"
        isOpen={open}
        isDismissable
        onOpenChange={setOpen}
      >
        <FilterControls {...props} />
      </Dialog>
    </>
  );
};
