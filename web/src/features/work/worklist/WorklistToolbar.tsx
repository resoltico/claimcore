import { SearchField } from "../../../ui/SearchField";
import { Toolbar } from "../../../ui/Toolbar";
import { useRef, useState, type ReactNode } from "react";
import { usePresentation } from "../../../presentation/context";
import {
  worklistSearchIssue,
  worklistSearchMaximum,
  type WorklistQuery,
} from "../../../routing/worklistQuery";
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
    <div className="worklist-search">
      <SearchField
        id="worklist-search"
        inputRef={input}
        label={p.text("ui.worklistSearch")}
        clearLabel={p.text("ui.clearFilters")}
        value={search}
        invalid={correction !== undefined}
        onSubmit={() => {
          if (issue !== undefined) {
            setSubmittedDraft(search);
            input.current?.focus();
          } else if (search !== (query.text ?? "")) {
            onChange(searchSelection(query, search));
          }
        }}
        onChange={(text) => {
          setSubmittedDraft(null);
          onSearch(text);
        }}
        onClear={() => {
          onChange(searchSelection(query, ""));
        }}
      />
      {correction === undefined ? null : (
        <p id="worklist-search-correction" className="worklist-search-correction" role="alert">
          {correction === "diagnostic.TOO_LONG"
            ? p.text(correction, { maximumCharacters: p.integer(worklistSearchMaximum) })
            : p.text(correction)}
        </p>
      )}
    </div>
  );
};
export const WorklistToolbar = (props: Props) => (
  <Toolbar
    className="worklist-toolbar"
    filter={
      <WorklistSearch
        query={props.query}
        onChange={props.onChange}
        search={props.search}
        onSearch={props.onSearch}
      />
    }
    activeFilters={
      Object.entries(props.query).filter(
        ([key, value]) =>
          key !== "view" &&
          key !== "text" &&
          value !== "" &&
          (!Array.isArray(value) || value.length > 0),
      ).length
    }
  >
    <WorklistFilters
      query={props.query}
      onChange={props.onChange}
      exportAction={props.exportAction}
    />
  </Toolbar>
);
