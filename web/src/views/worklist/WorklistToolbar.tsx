import { ReferenceSummary } from "../../components/ReferenceSummary";
import { useRef, useState } from "react";
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
  onSelect: (reference: string) => void;
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
      <button type="submit">{p.text("ui.search")}</button>
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
export const WorklistToolbar = (props: Props) => (
  <>
    <div className="worklist-toolbar">
      <WorklistSearch
        query={props.query}
        onChange={props.onChange}
        search={props.search}
        onSearch={props.onSearch}
      />
      <WorklistFilters query={props.query} onChange={props.onChange} />
    </div>
    <ExactLookup onSelect={props.onSelect} />
  </>
);
