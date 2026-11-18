import { apiEndpoints } from "../../api/service";
import { ScreenView } from "../../ui/ScreenView";
import type { CaseSummaryRequest } from "@contracts/client";
import { useEndpoint } from "../../hooks/useEndpoint";
import { usePresentation } from "../../presentation/context";
import { useState } from "react";
import { CsvExportButton } from "../../components/CsvExportButton";
import { Select } from "../../ui/Select";
import { queryInput } from "../../protocol/taskCatalog";
import { renderCatalogKey } from "../../presentation/messages";
import { isTerm } from "../../presentation/terms";
import { NoticeView } from "../../presentation/Message";
export const SummaryBand = ({ filter }: { filter: CaseSummaryRequest }) => {
  const p = usePresentation();
  const [groupBy, setGroupBy] = useState<CaseSummaryRequest["groupBy"]>();
  const selection = { ...filter, ...(groupBy === undefined ? {} : { groupBy }) };
  const query = useEndpoint(apiEndpoints.caseSummary, selection);
  const grouping = queryInput(apiEndpoints.caseSummary, "input:groupBy");
  const options = [
    { value: "", label: p.text("ui.anyFilter") },
    ...grouping.choices
      .filter((option) => isTerm("caseGrouping", option))
      .map((value) => ({ value, label: p.term("caseGrouping", value) })),
  ];
  const summary = query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.document : null;
  const content = () => {
    if (query.loading) {
      return (
        <p className="summary-band" role="status" aria-busy="true">
          {p.text("ui.loading")}
        </p>
      );
    }
    if (query.message !== null) {
      return (
        <p className="summary-band" role="alert">
          <NoticeView value={query.message} />
          <button type="button" onClick={query.reload}>
            {p.text("ui.tryAgain")}
          </button>
        </p>
      );
    }
    if (summary === null) {
      return null;
    }
    if (summary.basis.kind !== "SNAPSHOT") {
      return null;
    }
    const view = summary.screen;
    return view === null ? null : (
      <ScreenView view={view} timeZone={summary.basis.observation.timeZone} />
    );
  };

  return (
    <section className="summary-band" aria-label={p.text("ui.summaryDetails")}>
      <Select
        label={renderCatalogKey(p, grouping.labelId)}
        options={options}
        value={groupBy ?? ""}
        onChange={(value) => {
          setGroupBy(isTerm("caseGrouping", value) ? value : undefined);
        }}
      />
      {groupBy === undefined ? null : (
        <CsvExportButton
          selection={{ query: apiEndpoints.caseSummary, selection }}
          available={!query.loading && summary !== null}
        />
      )}
      {content()}
    </section>
  );
};
