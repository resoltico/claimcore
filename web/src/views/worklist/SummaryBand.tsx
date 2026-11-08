import { ScreenView } from "../../ui/ScreenView";
import type { CaseSummaryRequest } from "@contracts/client";
import { useEndpoint } from "../../hooks/useEndpoint";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
export const SummaryBand = ({ filter }: { filter: CaseSummaryRequest }) => {
  const p = usePresentation();
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
  const view = query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.view : null;
  return view === null ? null : (
    <section className="summary-band" aria-label={p.text("ui.summaryDetails")}>
      <ScreenView view={view} timeZone={summary.basis.timeZone} />
    </section>
  );
};
