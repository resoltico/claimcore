import { Modal, ModalOverlay } from "react-aria-components/Modal";
import { Dialog } from "react-aria-components/Dialog";
import { Heading } from "react-aria-components/Heading";
import { useState } from "react";
import { useCompact } from "../../hooks/useCompact";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { isTerm, isVocabulary } from "../../presentation/terms";
import { productMetadata } from "../../protocol/productMetadata";
import type { WorklistQuery } from "../../routing/worklistQuery";
type Props = { query: WorklistQuery; onChange: (query: WorklistQuery) => void };
const selectSet = (
  query: WorklistQuery,
  name: string,
  values: ReadonlyArray<string>,
): WorklistQuery => {
  const { view: _view, ...manual } = query;
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
const { inputs } = productMetadata.catalog.entries.find((entry) => entry.kind === "case.list")!;
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
  if (input.kind.kind !== "CHOICES") {
    return null;
  }
  const { vocabulary, options } = input.kind;
  const values = selectedValues(query, input.identity);
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
                    input.identity,
                    event.target.checked
                      ? [...values, option]
                      : values.filter((value) => value !== option),
                  ),
                );
              }}
            />
            {isVocabulary(vocabulary) && isTerm(vocabulary, option)
              ? p.term(vocabulary, option)
              : option}
          </label>
        ))}
      </fieldset>
    </details>
  );
};
const OrderControl = ({ query, onChange }: Props) => {
  const p = usePresentation();
  const options = inputs.flatMap((input) =>
    input.kind.kind === "CHOICE" && input.identity === "order" ? input.kind.options : [],
  );
  return (
    <label>
      {p.text("input.order.label")}
      <select
        value={query.order ?? "REFERENCE"}
        onChange={(event) => {
          const { view: _view, ...manual } = query;
          onChange({ ...manual, order: event.target.value === "RECENT" ? "RECENT" : "REFERENCE" });
        }}
      >
        {options.map((order) =>
          isTerm("caseOrder", order) ? (
            <option key={order} value={order}>
              {p.term("caseOrder", order)}
            </option>
          ) : null,
        )}
      </select>
    </label>
  );
};
const FilterControls = (props: Props) => (
  <div className="worklist-filters">
    {inputs.map((input) => (
      <FilterSet key={input.identity} input={input} {...props} />
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
        onClick={() => {
          setOpen(true);
        }}
      >
        {p.text("ui.filters")}
      </button>
      <ModalOverlay
        className="modal-overlay sheet-overlay"
        isOpen={open}
        isDismissable
        onOpenChange={setOpen}
      >
        <Modal className="filter-sheet">
          <Dialog aria-label={p.text("ui.filters")}>
            <Heading slot="title">{p.text("ui.filters")}</Heading>
            <FilterControls {...props} />
            <button
              type="button"
              onClick={() => {
                setOpen(false);
              }}
            >
              {p.text("ui.close")}
            </button>
          </Dialog>
        </Modal>
      </ModalOverlay>
    </>
  );
};
