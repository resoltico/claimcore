import { ScreenView } from "../../ui/ScreenView";
import type { CaseSummaryRequest } from "@contracts/client";
import { useEndpoint } from "../../hooks/useEndpoint";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { useCompact } from "../../hooks/useCompact";
export const SummaryBand = ({ filter }: { filter: CaseSummaryRequest }) => {
  const p = usePresentation();
  const compact = useCompact();
  const query = useEndpoint("case.summary", filter);
  const summary = query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.data : null;
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
      </p>
    );
  }
  if (summary === null) {
    return null;
  }
  const title = p.text("ui.summaryCounts", { count: p.integer(summary.counts.total) });
  const view = query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.view : null;
  const totals =
    view === null ? null : <ScreenView view={view} timeZone={summary.basis.timeZone} />;
  return compact ? (
    <details className="summary-band">
      <summary aria-label={`${title} · ${p.text("ui.summaryDetails")}`}>{title}</summary>
      {totals}
    </details>
  ) : (
    <section className="summary-band" aria-label={p.text("ui.summaryDetails")}>
      <strong>{title}</strong>
      {totals}
    </section>
  );
};
