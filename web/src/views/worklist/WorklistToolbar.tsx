import { ReferenceSummary } from "../../components/ReferenceSummary";
import { useEffect, useState } from "react";
import { usePresentation } from "../../presentation/context";
import { savedViews, viewQuery, type WorklistQuery } from "../../routing/worklistQuery";
import { WorklistFilters } from "./WorklistFilters";
import { savedViewLabel } from "./savedViewLabel";
type Props = {
  query: WorklistQuery;
  hasAssignedCases: boolean;
  onChange: (query: WorklistQuery) => void;
  onSelect: (reference: string) => void;
  loading: boolean;
  onReload: () => void;
};
const useSearchCommit = (text: string, query: WorklistQuery, onChange: Props["onChange"]) => {
  useEffect(() => {
    if (text === (query.text ?? "")) {
      return;
    }
    const timer = window.setTimeout(() => {
      const { view: _view, text: _text, ...manual } = query;
      onChange({ ...manual, ...(text === "" ? {} : { text }) });
    }, 300);
    return () => {
      window.clearTimeout(timer);
    };
  }, [text, query, onChange]);
};
const WorklistSearch = ({ query, onChange }: Pick<Props, "query" | "onChange">) => {
  const p = usePresentation();
  const source = query.text ?? "";
  const [draft, setDraft] = useState({ source, value: source });
  if (draft.source !== source) {
    setDraft({ source, value: source });
  }
  const text = draft.source === source ? draft.value : source;
  const commit = (value: string) => {
    const { view: _view, text: _text, ...manual } = query;
    onChange({ ...manual, ...(value === "" ? {} : { text: value }) });
  };
  useSearchCommit(text, query, onChange);
  return (
    <form
      role="search"
      className="worklist-search"
      onSubmit={(event) => {
        event.preventDefault();
        commit(text);
      }}
    >
      <label className="sr-only" htmlFor="worklist-search">
        {p.text("ui.worklistSearch")}
      </label>
      <input
        id="worklist-search"
        type="search"
        value={text}
        onChange={(event) => {
          setDraft({ source, value: event.target.value });
        }}
        placeholder={p.text("ui.worklistSearch")}
        autoComplete="off"
        dir="auto"
      />
      <button type="submit">{p.text("ui.search")}</button>
    </form>
  );
};
const ExactLookup = ({ onSelect }: Pick<Props, "onSelect">) => {
  const p = usePresentation();
  const [reference, setReference] = useState("");
  return (
    <details className="exact-lookup">
      <summary>{p.text("ui.findCase")}</summary>
      <form
        onSubmit={(event) => {
          event.preventDefault();
          if (reference !== "") {
            onSelect(reference);
          }
        }}
      >
        <label htmlFor="case-lookup">
          {p.text("ui.exactCaseReference", { label: p.fieldLabel("caseReference") })}
        </label>
        <input
          id="case-lookup"
          className="identifier-value"
          value={reference}
          onChange={(event) => {
            setReference(event.target.value);
          }}
          autoComplete="off"
          spellCheck={false}
          autoCapitalize="off"
          dir="ltr"
        />
        {reference === "" ? null : (
          <ReferenceSummary
            id="ui.exactReference"
            values={{ label: p.fieldLabel("caseReference"), reference }}
          />
        )}
        <button type="submit" disabled={reference === ""}>
          {p.text("ui.findCase")}
        </button>
      </form>
    </details>
  );
};
export const WorklistToolbar = (props: Props) => {
  const p = usePresentation();
  return (
    <>
      <div className="worklist-toolbar">
        <WorklistSearch query={props.query} onChange={props.onChange} />
        <WorklistFilters query={props.query} onChange={props.onChange} />
        <button type="button" disabled={props.loading} onClick={props.onReload}>
          {props.loading ? p.text("ui.loading") : p.text("ui.reloadCases")}
        </button>
      </div>
      <nav className="saved-views" aria-label={p.text("ui.savedViews")}>
        {savedViews
          .filter((view) => view !== "mine" || props.hasAssignedCases)
          .map((view) => (
            <button
              key={view}
              type="button"
              aria-pressed={
                props.query.view === view ||
                (view === "all" && Object.keys(props.query).length === 0)
              }
              onClick={() => {
                props.onChange(viewQuery(view));
              }}
            >
              {savedViewLabel(p, view)}
            </button>
          ))}
      </nav>
      <ExactLookup onSelect={props.onSelect} />
    </>
  );
};
