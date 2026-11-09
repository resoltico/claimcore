import { ScreenView } from "../../ui/ScreenView";
import type { CaseSummaryRequest } from "@contracts/client";
import { useEndpoint } from "../../hooks/useEndpoint";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
export const SummaryBand = ({ filter }: { filter: CaseSummaryRequest }) => {
  const p = usePresentation();
  const query = useEndpoint("case.summary", filter);
  const summary = query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.document : null;
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
  if (summary.basis.kind !== "SNAPSHOT") {
    return null;
  }
  const view = summary.screen;
  return view === null ? null : (
    <section className="summary-band" aria-label={p.text("ui.summaryDetails")}>
      <ScreenView view={view} timeZone={summary.basis.observation.timeZone} />
    </section>
  );
};
