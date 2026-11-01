import "./worklist/worklist.css";
import { useCallback, useMemo } from "react";
import type { ActorContext } from "@contracts/client";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { SummaryBand } from "./worklist/SummaryBand";
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
import { savedViewLabel } from "./worklist/savedViewLabel";
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
        <th scope="col">{p.text("ui.caseState")}</th>
        <th scope="col">{p.term("section", "work")}</th>
        <th scope="col">{p.text("ui.age")}</th>
        <th scope="col">{p.text("label.changedAt")}</th>
      </tr>
    </thead>
  );
};
type Page = ReturnType<typeof useWorklist>;
const WorklistEmpty = ({ page, filtered }: { page: Page; filtered: boolean }) => {
  const p = usePresentation();
  const emptyMessage =
    page.query.view === undefined
      ? p.text("ui.noCasesForView")
      : p.text("ui.noCasesForSavedView", { view: savedViewLabel(p, page.query.view) });
  return (
    <p>
      {emptyMessage}{" "}
      {filtered ? (
        <button
          type="button"
          onClick={() => {
            page.change({});
          }}
        >
          {p.text("ui.clearFilters")}
        </button>
      ) : null}
    </p>
  );
};
const WorklistFeedback = ({ page }: { page: Page }) => {
  const p = usePresentation();
  const filtered = Object.keys(page.query).some(
    (key) => key !== "view" || page.query.view !== "all",
  );
  const customEmpty = filtered || page.query.view !== undefined;
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
            empty={page.items.length === 0 && !customEmpty}
            cursor={page.cursor}
            emptyMessage="ui.noAccessibleCases"
          />
          {page.items.length === 0 && customEmpty ? (
            <WorklistEmpty page={page} filtered={filtered} />
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
const referenceSearchKey = (text: string | undefined) =>
  text?.replace(/[a-z]/gu, (letter) => letter.toUpperCase());
const ExactResult = ({
  value,
  onSelect,
}: {
  value: CaseSummary | null;
  onSelect: Props["onSelect"];
}) => {
  const p = usePresentation();
  if (value === null) {
    return null;
  }
  return (
    <button
      type="button"
      onClick={() => {
        onSelect(value.caseReference);
      }}
    >
      {p.text("ui.openExactCase", { reference: value.caseReference })}
    </button>
  );
};
const WorklistResults = ({
  page,
  onSelect,
  businessDate,
}: {
  page: Page;
  onSelect: Props["onSelect"];
  businessDate: string;
}) => {
  const p = usePresentation();
  const exact =
    page.items.length === 1 &&
    referenceSearchKey(page.query.text) === referenceSearchKey(page.items[0]?.caseReference)
      ? (page.items[0] ?? null)
      : null;
  return (
    <>
      <ExactResult value={exact} onSelect={onSelect} />
      <table className="worklist-table">
        <WorklistColumns />
        <tbody className="case-list worklist-rows">
          {page.items.map((value) => (
            <CaseRow
              key={value.caseReference}
              value={value}
              onSelect={onSelect}
              businessDate={businessDate}
            />
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
const ReadyCaseList = ({ onSelect, onOpen, context }: Props & { context: ActorContext }) => {
  const p = usePresentation();
  const page = useWorklist();
  return (
    <section aria-labelledby="case-list-title" className="worklist">
      <div className="section-heading">
        <h1 id="case-list-title">{p.text("ui.cases")}</h1>
        {page.mayOpen ? (
          <button className="primary" type="button" onClick={onOpen}>
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
      <SummaryBand filter={(({ view: _view, ...filters }) => filters)(page.query)} />
      <WorklistLoading page={page} />
      <WorklistResults
        page={page}
        onSelect={onSelect}
        businessDate={page.page?.basis.businessDate ?? context.businessDate}
      />
      <WorklistFeedback page={page} />
    </section>
  );
};

export const CaseList = (props: Props) => {
  const context = useRequesterContext();
  return context === null ? null : <ReadyCaseList {...props} context={context} />;
};
