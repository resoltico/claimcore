import { ViewTabs } from "../ui/ViewTabs";
import { isTerm } from "../presentation/terms";
import "./worklist/worklist.css";
import { useCallback, useEffect, useMemo, useState } from "react";
import type { CaseSelector, CaseRef } from "@contracts/client";
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
import { useWorkspaceViews, WorkspaceViewsStatus } from "./WorkspaceViews";
type Props = { onSelect: (target: CaseSelector) => void };
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
  };
};
const WorklistColumns = () => {
  const p = usePresentation();
  return (
    <thead className="worklist-columns">
      <tr>
        <th scope="col">{p.text("ui.caseParties")}</th>
        <th scope="col">{p.text("ui.caseState")}</th>
        <th scope="col">{p.text("ui.reminderAction")}</th>
        <th scope="col">{p.fieldLabel("responsibleActor")}</th>
        <th scope="col">{p.text("ui.claimedPayable")}</th>
        <th scope="col">{p.text("label.changedAt")}</th>
      </tr>
    </thead>
  );
};
type Page = ReturnType<typeof useWorklist>;
const WorklistEmpty = ({ page, filtered }: { page: Page; filtered: boolean }) => {
  const p = usePresentation();
  const emptyMessage =
    filtered || page.query.view === undefined
      ? p.text("ui.noCasesForView")
      : p.text(`view.${page.query.view}.empty`);
  return (
    <p>
      {emptyMessage}{" "}
      {filtered ? (
        <button
          type="button"
          onClick={() => {
            page.change(page.query.view === undefined ? {} : { view: page.query.view });
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
  const filtered = Object.keys(page.query).some((key) => key !== "view");
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
const ExactResult = ({
  value,
  onSelect,
}: {
  value: CaseRef | null;
  onSelect: Props["onSelect"];
}) => {
  const p = usePresentation();
  if (value === null) {
    return null;
  }
  return (
    <section className="worklist-exact-match" aria-label={p.text("label.exactReferenceMatch")}>
      <p>{p.text("label.exactReferenceMatch")}</p>
      <button
        type="button"
        onClick={() => {
          onSelect({ kind: "ID", caseId: value.caseId });
        }}
      >
        {p.text("ui.openExactCase", { reference: value.caseReference })}
      </button>
    </section>
  );
};
const WorklistResults = ({ page, onSelect }: { page: Page; onSelect: Props["onSelect"] }) => {
  const p = usePresentation();
  const exact = page.page?.exactReference ?? null;
  return (
    <>
      <ExactResult value={exact} onSelect={onSelect} />
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
const ReadyCaseList = ({ onSelect }: Props) => {
  const p = usePresentation();
  const page = useWorklist();
  const views = useWorkspaceViews().cases;
  const source = page.query.text ?? "";
  const [search, setSearch] = useState({ source, value: source });
  if (search.source !== source) {
    setSearch({ source, value: source });
  }
  return (
    <section aria-labelledby="case-list-title" className="worklist" aria-busy={page.loading}>
      <div className="section-heading">
        <div>
          <p className="eyebrow">{p.text("ui.workQueue")}</p>
          <h1 id="case-list-title">{p.text("ui.cases")}</h1>
          <p className="page-description">{p.text("ui.queueDescription")}</p>
        </div>
      </div>
      <WorkspaceViewsStatus area="cases" />
      <ViewTabs
        items={
          views.data?.items.map(({ view, count }) => ({
            id: view,
            label: p.term("caseView", view),
            count: p.integer(count),
          })) ?? []
        }
        selected={page.query.view ?? ""}
        label={p.text("input.caseView.label")}
        loading={views.loading}
        onSelect={(view) => {
          if (isTerm("caseView", view)) {
            page.change({ ...page.query, view });
          }
        }}
        controls={
          <WorklistToolbar
            query={page.query}
            search={search.value}
            onSearch={(value) => {
              setSearch({ source, value });
            }}
            onChange={page.change}
            onSelect={(caseReference) => {
              onSelect({ kind: "REFERENCE", caseReference });
            }}
            loading={page.loading}
            onReload={() => {
              void page.load(null);
            }}
          />
        }
      >
        <SummaryBand filter={page.query} />
        <WorklistLoading page={page} />
        <WorklistResults page={page} onSelect={onSelect} />
        <WorklistFeedback page={page} />
      </ViewTabs>
    </section>
  );
};

const DefaultCaseView = ({
  query,
  view,
}: {
  query: WorklistQuery;
  view: NonNullable<WorklistQuery["view"]>;
}) => {
  useEffect(() => {
    navigate(worklistPath({ ...query, view }), true);
  }, [query, view]);
  const p = usePresentation();
  return <p role="status">{p.text("ui.loadingCases")}</p>;
};
export const CaseList = (props: Props) => {
  const context = useRequesterContext();
  const views = useWorkspaceViews().cases;
  const route = useRoute();
  const query = useMemo(
    () => parseWorklistQuery(route.screen === "cases" ? route.query : ""),
    [route],
  );
  if (context === null) {
    return null;
  }
  if (query.view === undefined) {
    const first = views.data?.items[0];
    if (first !== undefined) {
      return <DefaultCaseView query={query} view={first.view} />;
    }
    return <WorkspaceViewsStatus area="cases" />;
  }
  return <ReadyCaseList {...props} />;
};
