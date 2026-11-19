import { apiEndpoints } from "../../api/service";
import { ScreenView } from "../../ui/ScreenView";
import { limits, type CaseSummarySelection } from "@contracts/client";
import { useEndpoint } from "../../hooks/useEndpoint";
import { usePresentation } from "../../presentation/context";
import { useState } from "react";
import { CsvExportButton } from "../../components/CsvExportButton";
import { Select } from "../../ui/Select";
import { queryInput } from "../../protocol/taskCatalog";
import { renderCatalogKey } from "../../presentation/messages";
import { isTerm } from "../../presentation/terms";
import { NoticeView } from "../../presentation/Message";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { useSessionContext } from "../../session/SessionContext";

const Report = ({
  selection,
  exportAvailable,
}: {
  selection: CaseSummarySelection;
  exportAvailable: boolean;
}) => {
  const p = usePresentation();
  const [cursor, setCursor] = useState<string>();
  const [restart, setRestart] = useState(0);
  const query = useEndpoint(
    apiEndpoints.caseSummary,
    { ...selection, limit: limits.maximumPageSize, ...(cursor === undefined ? {} : { cursor }) },
    true,
    String(restart),
  );
  const summary = query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.document : null;

  return (
    <>
      {selection.groupBy === undefined ? null : (
        <CsvExportButton
          selection={{ query: apiEndpoints.caseSummary, selection }}
          available={exportAvailable && query.value?.outcome.kind !== "REJECTED"}
        />
      )}
      {query.loading ? (
        <p className="summary-band" role="status" aria-busy="true">
          {p.text("ui.loading")}
        </p>
      ) : null}
      {query.message === null ? null : (
        <div className="summary-band" role="alert">
          <NoticeView value={query.message} />
          <button type="button" onClick={query.reload}>
            {p.text("ui.tryAgain")}
          </button>
        </div>
      )}
      {summary?.basis.kind === "SNAPSHOT" && summary.screen !== null && !query.loading ? (
        <ScreenView view={summary.screen} timeZone={summary.basis.observation.timeZone} />
      ) : null}
      {cursor === undefined ? null : (
        <button
          type="button"
          onClick={() => {
            setCursor(undefined);
            setRestart((count) => count + 1);
          }}
        >
          {p.text("ui.restartReport")}
        </button>
      )}
      {summary?.nextCursor == null || query.loading ? null : (
        <button
          type="button"
          onClick={() => {
            setCursor(summary.nextCursor ?? undefined);
          }}
        >
          {p.text("ui.nextReportPage")}
        </button>
      )}
    </>
  );
};

export const SummaryBand = ({
  filter,
  exportAvailable = true,
}: {
  filter: CaseSummarySelection;
  exportAvailable?: boolean;
}) => {
  const p = usePresentation();
  const requester = useRequesterContext();
  const { state } = useSessionContext();
  const [groupBy, setGroupBy] = useState<CaseSummarySelection["groupBy"]>();
  const selection = { ...filter, ...(groupBy === undefined ? {} : { groupBy }) };
  const grouping = queryInput(apiEndpoints.caseSummary, "input:groupBy");
  const options = [
    { value: "", label: p.text("ui.anyFilter") },
    ...grouping.choices
      .filter((option) => isTerm("caseGrouping", option))
      .map((value) => ({ value, label: p.term("caseGrouping", value) })),
  ];

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
      <Report
        key={JSON.stringify([
          requester?.subject.installationId,
          requester?.subject.actorId,
          state.epoch,
          selection,
        ])}
        selection={selection}
        exportAvailable={exportAvailable}
      />
    </section>
  );
};
