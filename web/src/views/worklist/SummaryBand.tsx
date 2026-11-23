import { Page } from "../../ui/Page";
import { apiEndpoints } from "../../api/service";
import { isDisclosureRefused } from "../../api/outcomes";
import type { ApiResult, EndpointOutcome } from "../../protocol/types";
import { ScreenView } from "../../ui/ScreenView";
import { limits, type CaseSummarySelection, type HumanRejection } from "@contracts/client";
import { useEndpoint } from "../../hooks/useEndpoint";
import { usePresentation } from "../../presentation/context";
import { useMemo, useRef, useState, useSyncExternalStore, type RefObject } from "react";
import { CsvExportButton } from "../../components/CsvExportButton";
import { Select } from "../../ui/Select";
import { queryInput } from "../../protocol/taskCatalog";
import { renderCatalogKey } from "../../presentation/messages";
import { isTerm } from "../../presentation/terms";
import { NoticeView } from "../../presentation/Message";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { useSessionContext } from "../../session/SessionContext";
import { parseWorklistQuery, worklistPath } from "../../routing/worklistQuery";
import { CaseSelectionRecovery } from "./CaseSelectionRecovery";
import { navigate, routeRevisionSnapshot, subscribeRoute } from "../../routing/navigation";
import { followLink } from "../../routing/followLink";
import { useSettingsDetour } from "../../hooks/SettingsDetourContext";

const isInvalidContinuation = (rejection: HumanRejection | null, cursor: string | undefined) =>
  cursor !== undefined &&
  rejection?.kind === "REFUSED" &&
  rejection.advice.message.messageId === "diagnostic.INVALID_CURSOR";

const useReportExportAvailable = (
  result: ApiResult<EndpointOutcome> | null,
  invalidContinuation: boolean,
) => {
  const [admitted, setAdmitted] = useState(false);
  if (result === null) {
    return admitted;
  }
  const authorized = result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED";
  const refused =
    isDisclosureRefused(result) ||
    (result.kind === "outcome" && result.value.outcome.kind === "REJECTED" && !invalidContinuation);
  if ((authorized || refused) && admitted !== authorized) {
    setAdmitted(authorized);
  }
  return authorized || (admitted && !refused);
};

type ReportPosition = { cursor?: string; page: number; restart: number };
type ReportProps = {
  selection: CaseSummarySelection;
  exportAvailable: boolean;
};
const ReportRead = ({
  selection,
  exportAvailable,
  position: { cursor, page, restart },
  routeRevision,
  move,
}: ReportProps & {
  position: ReportPosition;
  routeRevision: number;
  move: (button: HTMLButtonElement, cursor?: string) => void;
}) => {
  const p = usePresentation();
  const query = useEndpoint(
    apiEndpoints.caseSummary,
    { ...selection, limit: limits.maximumPageSize, ...(cursor === undefined ? {} : { cursor }) },
    true,
    `report:${routeRevision}:${restart}`,
  );
  const summary = query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.document : null;
  const outcome = query.value?.outcome;
  const invalidContinuation = isInvalidContinuation(
    outcome?.kind === "REJECTED" ? outcome.rejection : null,
    cursor,
  );
  const exportAuthorized = useReportExportAvailable(query.result, invalidContinuation);

  const next = summary?.nextCursor ?? null;
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
      <p className="sr-only" aria-live="polite" aria-atomic="true">
        {announcement()}
      </p>
      {selection.groupBy === undefined ? null : (
        <CsvExportButton
          selection={{ query: apiEndpoints.caseSummary, selection }}
          available={exportAvailable && exportAuthorized}
          label="ui.exportReportCsv"
        />
      )}
      {content()}
      {navigation()}
    </>
  );
};

const Report = ({
  title,
  ...props
}: ReportProps & { title: RefObject<HTMLHeadingElement | null> }) => {
  const [position, setPosition] = useState<ReportPosition>({ page: 1, restart: 0 });
  const routeRevision = useSyncExternalStore(subscribeRoute, routeRevisionSnapshot);
  const parked = useSettingsDetour()?.paused === true;
  const move = (button: HTMLButtonElement, cursor?: string) => {
    if (document.activeElement === button) {
      title.current?.focus({ preventScroll: true });
    }
    setPosition((current) =>
      cursor === undefined
        ? { page: 1, restart: current.restart + 1 }
        : { cursor, page: current.page + 1, restart: current.restart },
    );
  };
  return parked ? null : (
    <ReportRead
      key={routeRevision}
      {...props}
      position={position}
      routeRevision={routeRevision}
      move={move}
    />
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
  const title = useRef<HTMLHeadingElement>(null);
  const [groupBy, setGroupBy] = useState<CaseSummarySelection["groupBy"]>();
  const selection = { ...filter, ...(groupBy === undefined ? {} : { groupBy }) };
  const grouping = queryInput(apiEndpoints.caseSummary, "input:groupBy");
  const options = [
    { value: "", label: p.text("ui.anyFilter") },
    ...grouping.choices
      .filter((option) => isTerm("caseGrouping", option))
      .map((value) => ({ value, label: p.term("caseGrouping", value) })),
  ];
  const worklist = worklistPath(filter);
  const casesPath = worklist === "/cases" ? worklistPath({ view: "ALL" }) : worklist;

  return (
    <Page
      className="summary-band"
      as="section"
      labelledBy="case-report-title"
      id="case-report-title"
      title={p.text("ui.caseReport")}
      headingRef={title}
    >
      <Select
        label={renderCatalogKey(p, grouping.labelId)}
        options={options}
        value={groupBy ?? ""}
        onChange={(value) => {
          setGroupBy(isTerm("caseGrouping", value) ? value : undefined);
        }}
      />
      <a
        href={casesPath}
        onClick={(event) => {
          followLink(event, () => {
            navigate(casesPath);
          });
        }}
      >
        {p.text("label.cases")}
      </a>
      <Report
        key={JSON.stringify([
          requester?.subject.installationId,
          requester?.subject.actorId,
          state.epoch,
          selection,
        ])}
        selection={selection}
        exportAvailable={exportAvailable}
        title={title}
      />
    </Page>
  );
};

export const CaseReport = ({ query, available }: { query: string; available: boolean }) => {
  const { state } = useSessionContext();
  const selection = useMemo(() => parseWorklistQuery(query), [query]);
  if (state.kind !== "authenticated") {
    return null;
  }
  return selection.kind === "invalid" ? (
    <CaseSelectionRecovery key={query} admission={selection} report />
  ) : (
    <SummaryBand
      key={`${state.epoch}:${state.token}:${query}`}
      filter={selection.selection}
      exportAvailable={available}
    />
  );
};
