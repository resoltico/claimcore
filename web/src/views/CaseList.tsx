import type { MutationLockChange } from "../hooks/useWorkspaceMutationLock";
import { PresentedValue } from "../components/PresentedValue";
import { useSessionContext } from "../session/SessionContext";
import { casePath } from "../routing/routes";
import { ViewTabs } from "../ui/ViewTabs";
import { isTerm } from "../presentation/terms";
import "./worklist/worklist.css";
import { useCallback, useEffect, useMemo, useState } from "react";
import { Button } from "../ui/Button";
import { CaseReassignment, type ReassignmentSelection } from "./worklist/CaseReassignment";
import type { CaseSelector, HumanReadDocument_caseList } from "@contracts/client";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { SummaryBand } from "./worklist/SummaryBand";
import { PageStatus } from "../components/PageStatus";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import type { ServiceResponse } from "../api/service";
import { useEndpointPage } from "../hooks/useEndpointPage";
import { useContentRoute } from "../hooks/SettingsDetourContext";
import { navigate } from "../routing/navigation";
import {
  parseWorklistQuery,
  worklistPath,
  listRequest,
  type WorklistQuery,
} from "../routing/worklistQuery";
import { WorklistToolbar } from "./worklist/WorklistToolbar";
import { PageTables } from "../ui/PageTables";
import { useWorkspaceViews, WorkspaceViewsStatus } from "./WorkspaceViews";
type Props = {
  onSelect: (target: CaseSelector) => void;
  onMutationLockChange?: MutationLockChange;
  disclosureAvailable?: boolean;
};
const ignoreLock = (_locked: boolean): void => undefined;
export type CaseListControl = HumanReadDocument_caseList["context"]["cases"][number];
const casePage = (response: ServiceResponse<"case.list">) => {
  if (response.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const doc = response.outcome.document;
  if (doc.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    items: doc.context.cases,
    exactCaseId: doc.context.exactCaseId,
    nextCursor: doc.context.nextCursor,
    basis: doc.basis.observation,
    screen: doc.screen,
  };
};
const useWorklist = () => {
  const route = useContentRoute();
  const search = route.screen === "cases" ? route.query : "";
  const query = useMemo(() => parseWorklistQuery(search), [search]);
  const input = useCallback((cursor: string | null) => listRequest(query, cursor), [query]);
  const page = useEndpointPage<
    "case.list",
    CaseListControl,
    NonNullable<ReturnType<typeof casePage>>
  >("case.list", input, casePage);
  const change = useCallback((next: WorklistQuery) => {
    navigate(worklistPath(next), true);
  }, []);
  return {
    ...page,
    query,
    change,
  };
};
type Page = ReturnType<typeof useWorklist>;
const WorklistEmpty = ({ page, filtered }: { page: Page; filtered: boolean }) => {
  const p = usePresentation();
  const emptyMessage =
    filtered || page.query.view === undefined ? p.text("ui.noCasesForView") : p.text("view.empty");
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
const WorklistResults = ({
  page,
  onSelect,
  reassign,
  editing,
}: {
  page: Page;
  onSelect: Props["onSelect"];
  reassign: (row: CaseListControl) => void;
  editing: boolean;
}) => {
  const p = usePresentation();
  const exact = page.page?.exactCaseId ?? null;
  return (
    <>
      <PageTables
        views={page.views}
        {...(page.items.some((item) => item.assignment !== null)
          ? {
              renderActions: (key: string) => {
                const row = page.items.find((item) => item.caseId === key);
                return row !== undefined && row.assignment !== null ? (
                  <Button
                    id={`reassign-${row.caseId}`}
                    variant="secondary"
                    isDisabled={editing}
                    onPress={() => {
                      reassign(row);
                    }}
                  >
                    {p.text("ui.reassign")}
                  </Button>
                ) : null;
              },
            }
          : {})}
        {...(exact === null
          ? {}
          : {
              sectionLink: {
                column: "exactReference",
                href: casePath(exact),
                follow: () => {
                  onSelect({ kind: "ID", caseId: exact });
                },
              },
            })}
        rowLink={(key) => {
          const item = page.items.find((candidate) => candidate.caseId === key);
          return item === undefined
            ? undefined
            : {
                column: "caseReference",
                href: casePath(item.caseId),
                id: `case-${item.caseId}`,
                follow: () => {
                  onSelect({ kind: "ID", caseId: item.caseId });
                },
              };
        }}
      />
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
const ReadyCaseList = ({
  onSelect,
  onMutationLockChange = ignoreLock,
  disclosureAvailable = true,
}: Props) => {
  const p = usePresentation();
  const page = useWorklist();
  const [selection, setSelection] = useState<ReassignmentSelection | null>(null);
  const close = () => {
    const id = selection?.row.caseId;
    setSelection(null);
    requestAnimationFrame(() => {
      (
        document.getElementById(`reassign-${id ?? ""}`) ??
        document.getElementById("case-list-title")
      )?.focus();
    });
  };

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
          <h1 id="case-list-title" tabIndex={-1}>
            {p.text("ui.cases")}
          </h1>
          <p className="page-description">{p.text("ui.queueDescription")}</p>
        </div>
      </div>
      <WorkspaceViewsStatus area="cases" />
      <ViewTabs
        items={
          views.data?.items.map(({ view, label, count }) => ({
            id: view,
            label: <PresentedValue value={label} timeZone={views.data!.basis.timeZone} />,
            count: <PresentedValue value={count} timeZone={views.data!.basis.timeZone} />,
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
        <WorklistLoading page={page} />
        <WorklistResults
          page={page}
          onSelect={onSelect}
          editing={selection !== null}
          reassign={(row) => {
            setSelection({ row, readId: crypto.randomUUID() });
          }}
        />
        <SummaryBand filter={page.query} />
        <WorklistFeedback page={page} />
      </ViewTabs>
      {selection === null ? null : (
        <CaseReassignment
          key={selection.readId}
          selection={selection}
          disclosureAvailable={disclosureAvailable}
          close={close}
          onLockChange={onMutationLockChange}
          refused={() => {
            void page.load(null);
          }}
          recorded={close}
        />
      )}
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
const AvailableCaseList = (props: Props) => {
  const context = useRequesterContext();
  const views = useWorkspaceViews().cases;
  const route = useContentRoute();
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

/** List selection and editors belong to one authenticated session. */
export const CaseList = (props: Props) => {
  const { state } = useSessionContext();
  return state.kind === "authenticated" ? (
    <AvailableCaseList key={`${state.epoch}:${state.token}`} {...props} />
  ) : null;
};
