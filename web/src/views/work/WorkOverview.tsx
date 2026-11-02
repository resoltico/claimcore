import { useEndpointCache } from "../../hooks/EndpointProvider";
import type { WorkOverview as WorkOverviewData } from "@contracts/client";
import { useEffect, useRef } from "react";
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
/** A newly observed server business day refreshes date-dependent panes without inventing local dates. */
const useBusinessDayObservation = (value: WorkOverviewData | null) => {
  const date = value?.basis.businessDate ?? null;
  const cache = useEndpointCache();
  const previous = useRef<string | null>(null);
  useEffect(() => {
    if (date !== null) {
      const changed = previous.current !== null && previous.current !== date;
      previous.current = date;
      if (changed) {
        cache?.invalidate();
      }
    }
  }, [date, cache]);
};
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
const OverviewValues = ({ value, compact }: { value: WorkOverviewData; compact: boolean }) => {
  const p = usePresentation();
  if (!compact) {
    return (
      <>
        <OverviewCounts value={value} />
        <OverviewBasis value={value} />
      </>
    );
  }
  return (
    <>
      <dl className="work-attention-count">
        <div>
          <dt>{p.text("ui.attentionCases")}</dt>
          <dd>{p.integer(value.attentionCases)}</dd>
        </div>
      </dl>
      <OverviewBasis value={value} />
      <details>
        <summary>{p.text("ui.summaryDetails")}</summary>
        <p>{p.text("ui.workScopeHint")}</p>
        <OverviewCounts value={value} />
      </details>
    </>
  );
};
const OverviewHeading = ({ mine, compact }: { mine: boolean; compact: boolean }) => {
  const p = usePresentation();
  const scope = p.text(mine ? "ui.myWork" : "ui.accessibleWork");
  return compact ? (
    scope
  ) : (
    <>
      {p.text("ui.workOverview")} · {scope}
    </>
  );
};
const observedOverview = (
  overview: ReturnType<typeof useEndpoint<"work.overview">>,
): WorkOverviewData | null =>
  !overview.loading && overview.value?.outcome.kind === "SUCCEEDED"
    ? overview.value.outcome.data
    : null;
export const WorkOverview = ({
  mine = false,
  compact = false,
}: {
  mine?: boolean;
  compact?: boolean;
}) => {
  const p = usePresentation();
  const requester = useRequesterContext();
  const titleId = `work-overview-${String(mine)}`;
  const enabled = requester !== null && requester.views.includes("CASES");
  const overview = useEndpoint("work.overview", mine ? { mine: true } : {}, enabled);
  useWorkRefresh(overview.reload, enabled);
  const value = observedOverview(overview);
  useBusinessDayObservation(value);
  if (!enabled) {
    return null;
  }
  return (
    <section
      className={compact ? "work-overview compact-work-overview" : "work-overview"}
      aria-labelledby={titleId}
      aria-busy={overview.loading}
    >
      <div className="section-heading">
        <h2 id={titleId}>
          <OverviewHeading mine={mine} compact={compact} />
        </h2>
        <button type="button" disabled={overview.loading} onClick={overview.reload}>
          {p.text("ui.refreshWork")}
        </button>
      </div>
      {compact ? null : <p>{p.text("ui.workScopeHint")}</p>}
      {overview.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {overview.message === null ? null : (
        <p role="status">
          <NoticeView value={overview.message} />
        </p>
      )}
      {value === null ? (
        <p>{p.text("ui.workOverviewUnavailable")}</p>
      ) : (
        <OverviewValues value={value} compact={compact} />
      )}
    </section>
  );
};
