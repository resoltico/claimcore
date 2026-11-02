import { useEffect } from "react";
import { useEndpoint } from "../../hooks/useEndpoint";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";

/** Timers only request a new server observation. Business dates never come from this clock. */
const useWorkRefresh = (reload: () => void, enabled: boolean) => {
  useEffect(() => {
    if (!enabled) {
      return;
    }
    const resumed = () => {
      if (document.visibilityState === "visible") {
        reload();
      }
    };
    window.addEventListener("focus", resumed);
    document.addEventListener("visibilitychange", resumed);
    const timer = window.setInterval(resumed, 60_000);
    return () => {
      window.removeEventListener("focus", resumed);
      document.removeEventListener("visibilitychange", resumed);
      window.clearInterval(timer);
    };
  }, [reload, enabled]);
};
export const WorkOverview = ({ mine = false }: { mine?: boolean }) => {
  const p = usePresentation();
  const requester = useRequesterContext();
  const enabled = requester !== null && requester.views.includes("CASES");
  const overview = useEndpoint("work.overview", mine ? { mine: true } : {}, enabled);
  useWorkRefresh(overview.reload, enabled);
  if (!enabled) {
    return null;
  }
  const value =
    !overview.loading && overview.value?.outcome.kind === "SUCCEEDED"
      ? overview.value.outcome.data
      : null;
  const dimensions =
    value === null
      ? []
      : ([
          ["ui.actionableCases", value.actionableCases],
          ["ui.casesWithoutNextAction", value.missingPlanCases],
          ["ui.overdueCases", value.overdueCases],
          ["ui.overdueReminders", value.overdueReminders],
          ["ui.unassignedCases", value.unassignedCases],
          ["ui.unavailableAssigneeCases", value.unavailableAssigneeCases],
          ["ui.attentionCases", value.attentionCases],
        ] as const);
  return (
    <section
      className="work-overview"
      aria-labelledby="work-overview-title"
      aria-busy={overview.loading}
    >
      <div className="section-heading">
        <h2 id="work-overview-title">
          {p.text("ui.workOverview")} · {p.text(mine ? "ui.myWork" : "ui.accessibleWork")}
        </h2>
        <button type="button" disabled={overview.loading} onClick={overview.reload}>
          {p.text("ui.refreshWork")}
        </button>
      </div>
      <p>{p.text("ui.workScopeHint")}</p>
      {overview.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {overview.message === null ? null : (
        <p role="alert">
          <NoticeView value={overview.message} />
        </p>
      )}
      {value === null ? (
        <p>{p.text("ui.workOverviewUnavailable")}</p>
      ) : (
        <>
          <dl className="work-counts">
            {dimensions.map(([key, count]) => (
              <div key={key}>
                <dt>{p.text(key)}</dt>
                <dd>{p.integer(count)}</dd>
              </div>
            ))}
          </dl>
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
      )}
    </section>
  );
};
