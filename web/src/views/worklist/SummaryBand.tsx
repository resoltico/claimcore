import { apiEndpoints } from "../../api/service";
import { isDisclosureRefused } from "../../api/outcomes";
import type { ApiResult, EndpointOutcome } from "../../protocol/types";
import { ScreenView } from "../../ui/ScreenView";
import { limits, type CaseSummarySelection, type HumanRejection } from "@contracts/client";
import { useEndpoint } from "../../hooks/useEndpoint";
import { usePresentation } from "../../presentation/context";
import { useRef, useState } from "react";
import { CsvExportButton } from "../../components/CsvExportButton";
import { Select } from "../../ui/Select";
import { queryInput } from "../../protocol/taskCatalog";
import { renderCatalogKey } from "../../presentation/messages";
import { isTerm } from "../../presentation/terms";
import { NoticeView } from "../../presentation/Message";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { useSessionContext } from "../../session/SessionContext";

const isInvalidContinuation = (rejection: HumanRejection | null, cursor: string | undefined) =>
  cursor !== undefined &&
  rejection?.kind === "REFUSED" &&
  rejection.advice.message.messageId === "diagnostic.INVALID_CURSOR";

const reportDisclosureDecision = (result: ApiResult<EndpointOutcome> | null) => {
  if (result === null) {
    return null;
  }
  if (isDisclosureRefused(result)) {
    return true;
  }
  return result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED" ? false : null;
};

const useReportExportBlocked = (
  result: ApiResult<EndpointOutcome> | null,
  invalidContinuation: boolean,
) => {
  const [disclosureRefused, setDisclosureRefused] = useState(false);
  const disclosureDecision = reportDisclosureDecision(result);
  if (disclosureDecision !== null && disclosureDecision !== disclosureRefused) {
    setDisclosureRefused(disclosureDecision);
  }
  const selectionRejected =
    result?.kind === "outcome" && result.value.outcome.kind === "REJECTED" && !invalidContinuation;
  return selectionRejected || (disclosureDecision ?? disclosureRefused);
};

const Report = ({
  selection,
  exportAvailable,
}: {
  selection: CaseSummarySelection;
  exportAvailable: boolean;
}) => {
  const p = usePresentation();
  const title = useRef<HTMLHeadingElement>(null);
  const [position, setPosition] = useState<{
    cursor?: string;
    page: number;
    restart: number;
  }>({ page: 1, restart: 0 });
  const { cursor, page, restart } = position;
  const query = useEndpoint(
    apiEndpoints.caseSummary,
    { ...selection, limit: limits.maximumPageSize, ...(cursor === undefined ? {} : { cursor }) },
    true,
    String(restart),
  );
  const summary = query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.document : null;
  const outcome = query.value?.outcome;
  const invalidContinuation = isInvalidContinuation(
    outcome?.kind === "REJECTED" ? outcome.rejection : null,
    cursor,
  );
  const exportBlocked = useReportExportBlocked(query.result, invalidContinuation);

  const next = summary?.nextCursor ?? null;
  const move = (button: HTMLButtonElement, nextCursor?: string) => {
    if (document.activeElement === button) {
      title.current?.focus({ preventScroll: true });
    }
    setPosition((current) =>
      nextCursor === undefined
        ? { page: 1, restart: current.restart + 1 }
        : { cursor: nextCursor, page: current.page + 1, restart: current.restart },
    );
  };
  const announcement = () =>
    (page > 1 || restart > 0) && summary !== null && !query.loading
      ? p.text("ui.reportPage", { page: p.integer(page) })
      : "";
  const restartButton = () => (
    <button
      type="button"
      onClick={(event) => {
        move(event.currentTarget);
      }}
    >
      {p.text("ui.restartReport")}
    </button>
  );
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
        <div className="summary-band" role="alert">
          <NoticeView value={query.message} />
          {invalidContinuation ? (
            restartButton()
          ) : (
            <button type="button" onClick={query.reload}>
              {p.text("ui.tryAgain")}
            </button>
          )}
        </div>
      );
    }
    if (summary?.basis.kind !== "SNAPSHOT" || summary.screen === null) {
      return null;
    }
    return <ScreenView view={summary.screen} timeZone={summary.basis.observation.timeZone} />;
  };

  const navigation = () => (
    <>
      {cursor === undefined || invalidContinuation ? null : restartButton()}
      {next === null || query.loading ? null : (
        <button
          type="button"
          onClick={(event) => {
            move(event.currentTarget, next);
          }}
        >
          {p.text("ui.nextReportPage")}
        </button>
      )}
    </>
  );

  return (
    <>
      <h2 ref={title} tabIndex={-1}>
        {p.text("ui.caseReport")}
      </h2>
      <p className="sr-only" aria-live="polite" aria-atomic="true">
        {announcement()}
      </p>
      {selection.groupBy === undefined ? null : (
        <CsvExportButton
          selection={{ query: apiEndpoints.caseSummary, selection }}
          available={exportAvailable && !exportBlocked}
        />
      )}
      {content()}
      {navigation()}
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
