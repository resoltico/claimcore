import "./worklist/worklist.css";
import { useCallback, useMemo } from "react";
import { PageStatus } from "../components/PageStatus";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import type { CaseSummary, ServiceResponse } from "../api/service";
import { useEndpointPage } from "../hooks/useEndpointPage";
import { useRoute } from "../routing/useRoute";
import { navigate } from "../routing/navigation";
import {
  parseWorklistQuery,
  worklistPath,
  listRequest,
  type WorklistQuery,
} from "../routing/worklistQuery";
import { WorklistToolbar } from "./worklist/WorklistToolbar";
import { CaseRow } from "./worklist/CaseRow";
type Props = { onSelect: (caseReference: string) => void; onOpen: () => void };
const casePage = (response: ServiceResponse<"case.list">) =>
  response.outcome.kind === "SUCCEEDED" ? response.outcome.data : null;
const useWorklist = () => {
  const route = useRoute();
  const search = route.screen === "cases" ? route.query : "";
  const query = useMemo(() => parseWorklistQuery(search), [search]);
  const input = useCallback((cursor: string | null) => listRequest(query, cursor), [query]);
  const page = useEndpointPage<"case.list", CaseSummary, NonNullable<ReturnType<typeof casePage>>>(
    "case.list",
    input,
    casePage,
  );
  const change = useCallback((next: WorklistQuery) => {
    navigate(worklistPath(next), true);
  }, []);
  return {
    ...page,
    query,
    change,
    mayOpen: page.page?.offers.some((offer) => offer.kind === "OPEN") ?? false,
  };
};
const WorklistColumns = () => {
  const p = usePresentation();
  return (
    <thead className="worklist-columns">
      <tr>
        <th scope="col">{p.fieldLabel("caseReference")}</th>
        <th scope="col">{p.fieldLabel("claimantName")}</th>
        <th scope="col">{p.fieldLabel("claimedAmount")}</th>
        <th scope="col">{p.fieldLabel("payableAmount")}</th>
        <th scope="col">{p.text("input.progress.label")}</th>
        <th scope="col">{p.text("label.changedAt")}</th>
      </tr>
    </thead>
  );
};
type Page = ReturnType<typeof useWorklist>;
const WorklistFeedback = ({ page }: { page: Page }) => {
  const p = usePresentation();
  const filtered = Object.keys(page.query).some(
    (key) => key !== "view" || page.query.view !== "all",
  );
  return (
    <>
      {page.message === null ? null : (
        <div role="alert">
          <NoticeView value={page.message} />
          <button
            type="button"
            onClick={() => {
              void page.load(null);
            }}
          >
            {p.text("ui.tryAgain")}
          </button>
        </div>
      )}
      {page.page !== null && !page.loading && page.message === null ? (
        <>
          <PageStatus
            empty={page.items.length === 0}
            cursor={page.cursor}
            emptyMessage="ui.noAccessibleCases"
          />
          {page.items.length === 0 && filtered ? (
            <p>
              {p.text("ui.noCasesForView")}{" "}
              <button
                type="button"
                onClick={() => {
                  page.change({});
                }}
              >
                {p.text("ui.clearFilters")}
              </button>
            </p>
          ) : null}
        </>
      ) : null}
    </>
  );
};
const WorklistLoading = ({ page }: { page: Page }) => {
  const p = usePresentation();
  return page.loading ? (
    <>
      <div className="worklist-skeleton" aria-hidden="true" />
      <p role="status">{p.text("ui.loadingCases")}</p>
    </>
  ) : null;
};
const WorklistResults = ({ page, onSelect }: { page: Page; onSelect: Props["onSelect"] }) => {
  const p = usePresentation();
  const exact =
    page.items.length === 1 && page.query.text === page.items[0]?.caseReference
      ? (page.items[0] ?? null)
      : null;
  return (
    <>
      {exact === null ? null : (
        <button
          type="button"
          onClick={() => {
            onSelect(exact.caseReference);
          }}
        >
          {p.text("ui.openExactCase", { reference: exact.caseReference })}
        </button>
      )}
      <table className="worklist-table">
        <WorklistColumns />
        <tbody className="case-list worklist-rows">
          {page.items.map((value) => (
            <CaseRow key={value.caseReference} value={value} onSelect={onSelect} />
          ))}
        </tbody>
      </table>
      {page.cursor === null ? null : (
        <button
          type="button"
          disabled={page.loading}
          onClick={() => {
            void page.load(page.cursor);
          }}
        >
          {p.text("ui.moreCases")}
        </button>
      )}
    </>
  );
};
export const CaseList = ({ onSelect, onOpen }: Props) => {
  const p = usePresentation();
  const page = useWorklist();
  return (
    <section aria-labelledby="case-list-title" className="worklist">
      <div className="section-heading">
        <h1 id="case-list-title">{p.text("ui.cases")}</h1>
        {page.mayOpen ? (
          <button type="button" onClick={onOpen}>
            {p.actLabel("OPEN")}
          </button>
        ) : null}
      </div>
      <WorklistToolbar
        query={page.query}
        onChange={page.change}
        onSelect={onSelect}
        loading={page.loading}
        onReload={() => {
          void page.load(null);
        }}
      />
      <WorklistFeedback page={page} />
      <WorklistLoading page={page} />
      <WorklistResults page={page} onSelect={onSelect} />
    </section>
  );
};
