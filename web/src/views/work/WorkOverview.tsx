import type { WorkOverview as WorkOverviewData } from "@contracts/client";
import { useEndpoint } from "../../hooks/useEndpoint";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";

const OverviewCounts = ({ value }: { value: WorkOverviewData }) => {
  const p = usePresentation();
  const dimensions = [
    ["ui.actionableCases", value.actionableCases],
    ["ui.casesWithoutNextAction", value.missingPlanCases],
    ["ui.overdueCases", value.overdueCases],
    ["ui.overdueReminders", value.overdueReminders],
    ["ui.unassignedCases", value.unassignedCases],
    ["ui.unavailableAssigneeCases", value.unavailableAssigneeCases],
    ["ui.attentionCases", value.attentionCases],
  ] as const;
  return (
    <dl className="work-counts">
      {dimensions.map(([key, count]) => (
        <div key={key}>
          <dt>{p.text(key)}</dt>
          <dd>{p.integer(count)}</dd>
        </div>
      ))}
    </dl>
  );
};
const OverviewBasis = ({ value }: { value: WorkOverviewData }) => {
  const p = usePresentation();
  return (
    <>
      <p className="observation-basis">
        {p.text("ui.workDateBasis", {
          date: p.date(value.basis.businessDate),
          zone: value.basis.timeZone,
        })}
      </p>
      <p className="observation-basis">
        {p.text("ui.observationInstant", {
          instant: p.recordedTime(value.basis.observedAt, value.basis.timeZone),
        })}
      </p>
    </>
  );
};
const OverviewHeading = ({ mine }: { mine: boolean }) => {
  const p = usePresentation();
  return (
    <>
      {p.text("ui.workOverview")} · {p.text(mine ? "ui.myWork" : "ui.accessibleWork")}
    </>
  );
};
const observedOverview = (
  overview: ReturnType<typeof useEndpoint<"work.overview">>,
): WorkOverviewData | null =>
  !overview.loading && overview.value?.outcome.kind === "SUCCEEDED"
    ? overview.value.outcome.data
    : null;
export const WorkOverview = ({ mine = false }: { mine?: boolean }) => {
  const p = usePresentation();
  const requester = useRequesterContext();
  const titleId = `work-overview-${String(mine)}`;
  const enabled = requester !== null && requester.views.includes("CASES");
  const overview = useEndpoint("work.overview", mine ? { mine: true } : {}, enabled);
  const value = observedOverview(overview);
  if (!enabled) {
    return null;
  }
  return (
    <section className="work-overview" aria-labelledby={titleId} aria-busy={overview.loading}>
      <div className="section-heading">
        <h2 id={titleId}>
          <OverviewHeading mine={mine} />
        </h2>
        <button type="button" disabled={overview.loading} onClick={overview.reload}>
          {p.text("ui.refreshWork")}
        </button>
      </div>
      <p>{p.text("ui.workScopeHint")}</p>
      {overview.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {overview.message === null ? null : (
        <p role="status">
          <NoticeView value={overview.message} />
        </p>
      )}
      {value === null ? (
        <p>{p.text("ui.workOverviewUnavailable")}</p>
      ) : (
        <>
          <OverviewCounts value={value} />
          <OverviewBasis value={value} />
        </>
      )}
    </section>
  );
};
