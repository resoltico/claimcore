import { ReferenceSummary } from "../../components/ReferenceSummary";
import { useEffect, useState } from "react";
import { usePresentation } from "../../presentation/context";
import { type WorklistQuery } from "../../routing/worklistQuery";
import { WorklistFilters } from "./WorklistFilters";
type Props = {
  query: WorklistQuery;
  search: string;
  onSearch: (value: string) => void;
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
      const { text: _text, ...manual } = query;
      onChange({ ...manual, ...(text === "" ? {} : { text }) });
    }, 300);
    return () => {
      window.clearTimeout(timer);
    };
  }, [text, query, onChange]);
};
const WorklistSearch = ({
  query,
  onChange,
  search,
  onSearch,
}: Pick<Props, "query" | "onChange" | "search" | "onSearch">) => {
  const p = usePresentation();
  const text = search;
  const commit = (value: string) => {
    const { text: _text, ...manual } = query;
    onChange({ ...manual, ...(value === "" ? {} : { text: value }) });
  };
  useSearchCommit(text, query, onChange);
  return (
    <form
      role="search"
      aria-label={p.text("ui.worklistSearch")}
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
          onSearch(event.target.value);
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
        <WorklistSearch
          query={props.query}
          onChange={props.onChange}
          search={props.search}
          onSearch={props.onSearch}
        />
        <WorklistFilters query={props.query} onChange={props.onChange} />
        <button type="button" disabled={props.loading} onClick={props.onReload}>
          {props.loading ? p.text("ui.loading") : p.text("ui.reloadCases")}
        </button>
      </div>
      <ExactLookup onSelect={props.onSelect} />
    </>
  );
};
