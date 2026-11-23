import { Toolbar } from "../../ui/Toolbar";
import { useRef, useState, type ReactNode } from "react";
import { usePresentation } from "../../presentation/context";
import {
  worklistSearchIssue,
  worklistSearchMaximum,
  type WorklistQuery,
} from "../../routing/worklistQuery";
import { WorklistFilters } from "./WorklistFilters";
type Props = {
  query: WorklistQuery;
  search: string;
  onSearch: (value: string) => void;
  onChange: (query: WorklistQuery) => void;
  exportAction?: ReactNode;
};
const searchSelection = (query: WorklistQuery, text: string): WorklistQuery => {
  const { text: _text, ...filters } = query;
  return text === "" ? filters : { ...filters, text };
};

const WorklistSearch = ({
  query,
  onChange,
  search,
  onSearch,
}: Pick<Props, "query" | "onChange" | "search" | "onSearch">) => {
  const p = usePresentation();
  const input = useRef<HTMLInputElement>(null);
  const [submittedDraft, setSubmittedDraft] = useState<string | null>(null);
  const issue = worklistSearchIssue(search);
  const correction = submittedDraft === search ? issue : undefined;

  return (
    <form
      role="search"
      aria-label={p.text("ui.worklistSearch")}
      className="worklist-search"
      onSubmit={(event) => {
        event.preventDefault();
        if (issue !== undefined) {
          setSubmittedDraft(search);
          input.current?.focus();
        } else if (search !== (query.text ?? "")) {
          onChange(searchSelection(query, search));
        }
      }}
    >
      <label htmlFor="worklist-search" className="worklist-search-scope">
        {p.text("ui.worklistSearch")} ·{" "}
        {query.view === undefined ? p.term("caseView", "ALL") : p.term("caseView", query.view)}
      </label>
      <input
        id="worklist-search"
        type="search"
        ref={input}
        value={search}
        aria-invalid={correction === undefined ? undefined : true}
        aria-describedby={correction === undefined ? undefined : "worklist-search-correction"}
        onChange={(event) => {
          setSubmittedDraft(null);
          onSearch(event.target.value);
        }}
        placeholder={p.text("ui.worklistSearch")}
        autoComplete="off"
        dir="auto"
      />
      {search === "" ? null : (
        <button
          type="button"
          onClick={() => {
            onSearch("");
            setSubmittedDraft(null);
            onChange(searchSelection(query, ""));
            input.current?.focus();
          }}
        >
          {p.text("ui.clearFilters")}
        </button>
      )}
      {correction === undefined ? null : (
        <p id="worklist-search-correction" className="worklist-search-correction" role="alert">
          {correction === "diagnostic.TOO_LONG"
            ? p.text(correction, { maximumCharacters: p.integer(worklistSearchMaximum) })
            : p.text(correction)}
        </p>
      )}
    </form>
  );
};
export const WorklistToolbar = (props: Props) => (
  <Toolbar className="worklist-toolbar">
    <WorklistSearch
      query={props.query}
      onChange={props.onChange}
      search={props.search}
      onSearch={props.onSearch}
    />
    <WorklistFilters query={props.query} onChange={props.onChange} />
    {props.exportAction}
  </Toolbar>
);
