import type { CaseSummaryRequest, CaseWorklistSummary, CurrencyTotal } from "@contracts/client";
import { useEndpoint } from "../../hooks/useEndpoint";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { useCompact } from "../../hooks/useCompact";
const Totals = ({ values }: { values: ReadonlyArray<CurrencyTotal> }) => {
  const p = usePresentation();
  return (
    <>
      {values.map((value) => (
        <bdi dir="ltr" key={value.currency}>
          {p.money(value.amount, value.currency)}
        </bdi>
      ))}
    </>
  );
};
const SummaryTotals = ({ summary }: { summary: CaseWorklistSummary }) => {
  const p = usePresentation();
  return (
    <dl className="summary-totals">
      <div>
        <dt>{p.text("ui.claimedTotal")}</dt>
        <dd>
          <Totals values={summary.claimed} />
        </dd>
      </div>
      <div>
        <dt>{p.text("ui.payableTotal")}</dt>
        <dd>
          <Totals values={summary.payable} />
        </dd>
      </div>
    </dl>
  );
};
const WorkCounts = ({ summary }: { summary: CaseWorklistSummary }) => {
  const p = usePresentation();
  return (
    <>
      <dl className="work-counts">
        {summary.counts.assignment.map((value) => (
          <div key={value.value}>
            <dt>{p.term("assignment", value.value)}</dt>
            <dd>{p.integer(value.count)}</dd>
          </div>
        ))}
        {summary.counts.due.map((value) => (
          <div key={value.value}>
            <dt>{p.term("due", value.value)}</dt>
            <dd>{p.integer(value.count)}</dd>
          </div>
        ))}
      </dl>
      <p className="observation-basis">
        {p.text("ui.summaryDateBasis", {
          date: p.date(summary.basis.businessDate),
          zone: summary.basis.timeZone,
        })}
      </p>
    </>
  );
};
export const SummaryBand = ({ filter }: { filter: CaseSummaryRequest }) => {
  const p = usePresentation();
  const compact = useCompact();
  const query = useEndpoint("case.summary", filter);
  const summary = query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.data : null;
  if (query.loading) {
    return (
      <p className="summary-band" role="status">
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
  const totals = (
    <>
      <SummaryTotals summary={summary} />
      <WorkCounts summary={summary} />
    </>
  );
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
