import { useDocumentTitle } from "../../hooks/useDocumentTitle";
import type { ReactNode } from "react";
import { Button } from "../../ui/Button";
import { EmptyState } from "../../ui/EmptyState";
import { PhoneAction } from "../../ui/PhoneAction";
import { Icon } from "../../ui/icons";
import { Page, PageHeading } from "../../ui/Page";
import { CsvExportButton } from "../../components/CsvExportButton";
import { apiEndpoints, type ServiceResponse } from "../../api/service";
import { type BoundOperationAffordance, hasOperationBinding } from "../../operation/affordances";
import { caseLinkTarget, caseLinkView, caseTargetPath } from "../../routing/coreLinks";
import type { RegionRow, CaseSelector } from "@contracts/client";
import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";
import { PresentedValue } from "../../components/PresentedValue";
import { useSessionContext } from "../../session/SessionContext";
import { rememberWorklist } from "../../routing/lastWorklist";
import { ListFooter } from "../../ui/ListFooter";
import { ViewTabs } from "../../ui/ViewTabs";
import { isTerm } from "../../presentation/terms";
import {} from "./worklist/worklist.css";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { CaseReassignment, type ReassignmentSelection } from "./worklist/CaseReassignment";

import { useRequesterContext, useCaseCreationControl } from "../../hooks/useRequesterContext";
import { operationTask } from "../../protocol/taskCatalog";
import { renderCatalogKey } from "../../presentation/messages";
import { followLink } from "../../routing/followLink";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";

import { useEndpointPage } from "../../hooks/useEndpointPage";
import { useFocusActivation } from "../../routing/useRoute";
import { useContentRoute } from "../../hooks/SettingsDetourContext";
import { navigate } from "../../routing/navigation";
import {
  parseWorklistQuery,
  worklistPath,
  listRequest,
  type WorklistQuery,
} from "../../routing/worklistQuery";
import { CaseSelectionRecovery } from "./worklist/CaseSelectionRecovery";
import { WorklistToolbar } from "./worklist/WorklistToolbar";
import { ResultPages } from "../../ui/ResultPages";
import { ScreenView } from "../../ui/ScreenView";
import { useWorkspaceViews, WorkspaceViewsStatus } from "./WorkspaceViews";
type Props = {
  onOpenCase?: () => void;
  queryOverride?: string | undefined;
  onSelect: (target: CaseSelector, originId?: string) => void;
  onMutationLockChange?: MutationLockChange;
  disclosureAvailable?: boolean;
};
const ignoreLock = (_locked: boolean): void => undefined;
export type CaseListControl = BoundOperationAffordance<"WORK">;
const casePage = (response: ServiceResponse<typeof apiEndpoints.caseList>) => {
  if (response.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const doc = response.outcome.document;
  if (doc.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    items: doc.screen.regions.flatMap((region) =>
      region.content.kind === "TABLE" ? region.content.rows : [],
    ),
    exactTarget:
      doc.links.flatMap((named) =>
        named.rel === "OPEN_CASE" ? (caseLinkTarget(named.link) ?? []) : [],
      )[0] ?? null,
    nextCursor: doc.nextCursor,
    basis: doc.basis.observation,
    screen: doc.screen,
    disclosure: doc.disclosure,
  };
};
const useWorklist = (query: WorklistQuery) => {
  const input = useCallback((cursor: string | null) => listRequest(query, cursor), [query]);
  const page = useEndpointPage<
    typeof apiEndpoints.caseList,
    RegionRow,
    NonNullable<ReturnType<typeof casePage>>
  >(apiEndpoints.caseList, input, casePage);
  const change = useCallback((next: WorklistQuery) => {
    const path = worklistPath(next);
    navigate(path === "/cases" ? worklistPath({ view: "ALL" }) : path);
  }, []);
  return {
    ...page,
    query,
    change,
  };
};
type CaseListPage = ReturnType<typeof useWorklist>;
const pageIsEmpty = (page: CaseListPage) =>
  page.page !== null && !page.loading && page.message === null && page.items.length === 0;

const worklistHeaderAction = (page: CaseListPage, empty: boolean, primary: ReactNode) =>
  empty || page.page === null ? null : primary;

const WorklistEmpty = ({
  page,
  filtered,
  action,
}: {
  page: CaseListPage;
  filtered: boolean;
  action: ReactNode;
}) => {
  const p = usePresentation();
  const emptyId = page.page?.screen.regions.find((region) => region.role === "RESULTS")?.emptyId;
  const { view, order } = page.query;
  return (
    <EmptyState
      messageId={emptyId ?? "label.noCasesForView"}
      action={
        <>
          {action}
          {filtered ? (
            <Button
              variant="secondary"
              onPress={() => {
                page.change({
                  ...(view === undefined ? {} : { view }),
                  ...(order === undefined ? {} : { order }),
                });
              }}
            >
              {p.text("ui.clearFilters")}
            </Button>
          ) : null}
        </>
      }
    />
  );
};
const WorklistFeedback = ({ page, action }: { page: CaseListPage; action: ReactNode }) => {
  const p = usePresentation();
  const filtered = Object.keys(page.query).some((key) => key !== "view" && key !== "order");
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
      {page.page === null ||
      page.loading ||
      page.message !== null ||
      page.items.length > 0 ? null : (
        <WorklistEmpty page={page} filtered={filtered} action={action} />
      )}
    </>
  );
};
const WorklistLoading = ({ page }: { page: CaseListPage }) => {
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
  page: CaseListPage;
  onSelect: Props["onSelect"];
  reassign: (row: CaseListControl) => void;
  editing: boolean;
}) => {
  const current = page.page;
  const exact = current?.exactTarget ?? null;
  const exactOrigin = "case-exact-match";
  return (
    <>
      {page.items.length === 0 && exact === null ? null : (
        <ResultPages
          showTableSections={false}
          views={page.views}
          roles={["RESULTS"]}
          actionPresentation={(action) => {
            if (action.binding.kind !== "WORK" || action.binding.target.kind !== "ID") {
              return {};
            }
            return { id: `reassign-${action.binding.target.caseId}` };
          }}
          linkNavigation={(link) => {
            const target = caseLinkTarget(link);
            return target?.kind === "ID"
              ? {
                  id: `case-${target.caseId}`,
                  follow: () => {
                    onSelect(target);
                  },
                }
              : undefined;
          }}
          onAction={(action) => {
            if (!editing && hasOperationBinding(action, "WORK")) {
              reassign(action);
            }
          }}
        />
      )}
      {current === null || exact === null ? null : (
        <ScreenView
          view={{
            regions: current.screen.regions.filter(
              ({ content }) =>
                content.kind === "ENTRIES" &&
                content.entries.some(({ key }) => key === "exactReference"),
            ),
          }}
          timeZone={current.basis.timeZone}
          roles={["FACTS"]}
          sectionLink={{
            column: "exactReference",
            id: exactOrigin,
            href: caseTargetPath(exact),
            follow: () => {
              onSelect(exact, exactOrigin);
            },
          }}
        />
      )}
      {page.page === null ? null : (
        <ListFooter
          subject="cases"
          count={page.items.length}
          basis={page.page.basis}
          continuation={page.cursor}
          loading={page.loading}
          load={(cursor) => {
            void page.load(cursor);
          }}
          busy={page.refreshBusy}
          refresh={page.refresh}
        />
      )}
    </>
  );
};
const CaseCreation = ({
  onOpenCase,
  available,
}: {
  onOpenCase: Props["onOpenCase"];
  available: boolean;
}) => {
  const p = usePresentation();
  const action = useCaseCreationControl();
  if (action === undefined || onOpenCase === undefined) {
    return null;
  }
  const task = operationTask(action.task);
  const label = renderCatalogKey(p, action.labelId);
  const link = (phone: boolean) => (
    <a
      id="open-case"
      className="ui-button ui-button--primary open-case-link"
      href="/cases/new"
      aria-label={label}
      aria-disabled={!available || undefined}
      onClick={(event) => {
        followLink(event, () => {
          if (available) {
            onOpenCase();
          }
        });
      }}
    >
      {phone && task.creates ? <Icon name="add" /> : label}
    </a>
  );
  return <PhoneAction renderCompact={() => link(true)}>{link(false)}</PhoneAction>;
};

const CaseListHeading = ({ action }: { action: ReactNode }) => {
  const p = usePresentation();
  const views = useWorkspaceViews().cases.data;
  const all = views?.items.find(({ view }) => view === "ALL")?.count;
  const needed = views?.navigation;
  const summary =
    all?.kind === "COUNT" && needed?.kind === "COUNT"
      ? p.text("label.caseWorklistCaption", {
          attention: p.integer(needed.value),
          total: p.integer(all.value),
        })
      : undefined;
  return (
    <PageHeading
      id="case-list-title"
      title={p.text("label.cases")}
      subtitle={summary}
      primaryAction={action}
    />
  );
};
const useSearchDraft = (source: string) => {
  const activation = useFocusActivation();
  const [search, setSearch] = useState({ source, value: source, revision: activation.revision });
  if (search.source !== source || search.revision !== activation.revision) {
    const reset = search.source !== source || activation.id === "worklist-search";
    setSearch({ source, value: reset ? source : search.value, revision: activation.revision });
  }
  const change = (value: string) => {
    setSearch({ ...search, value });
  };
  return [search.value, change] as const;
};
const ReadyCaseList = ({
  onSelect,
  onOpenCase,
  onMutationLockChange = ignoreLock,
  disclosureAvailable = true,
  query,
}: Props & { query: WorklistQuery }) => {
  const p = usePresentation();
  const page = useWorklist(query);
  useEffect(() => {
    rememberWorklist(worklistPath(query));
  }, [query]);

  const [selection, setSelection] = useState<ReassignmentSelection | null>(null);
  const close = () => {
    const id =
      selection?.row.binding.target.kind === "ID" ? selection.row.binding.target.caseId : undefined;
    setSelection(null);
    requestAnimationFrame(() => {
      (
        document.getElementById(`reassign-${id ?? ""}`) ??
        document.getElementById("case-list-title")
      )?.focus();
    });
  };

  const views = useWorkspaceViews().cases;
  const quickViews = views.data?.items ?? [];
  const activeView = quickViews.find(({ view }) => view === page.query.view)?.view ?? "";
  const source = page.query.text ?? "";
  const [search, setSearch] = useSearchDraft(source);
  const empty = pageIsEmpty(page);
  const primary = <CaseCreation onOpenCase={onOpenCase} available={disclosureAvailable} />;
  return (
    <Page
      className="worklist"
      busy={page.loading}
      header={<CaseListHeading action={worklistHeaderAction(page, empty, primary)} />}
    >
      <WorkspaceViewsStatus area="cases" />

      <ViewTabs
        items={quickViews.map(({ view, label, count, prominence }) => ({
          id: view,
          prominence,
          menuLabel:
            label.kind === "MESSAGE" && count.kind === "COUNT"
              ? p.text("ui.viewWithCount", {
                  view: renderCatalogKey(p, label.messageId),
                  count: p.integer(count.value),
                })
              : undefined,
          label: <PresentedValue value={label} timeZone={views.data!.basis.timeZone} />,
          count: <PresentedValue value={count} timeZone={views.data!.basis.timeZone} />,
        }))}
        selected={activeView}
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
            search={search}
            onSearch={setSearch}
            onChange={page.change}
            exportAction={
              <CsvExportButton
                selection={{ query: apiEndpoints.caseList, selection: page.query }}
                available={disclosureAvailable && !page.loading && page.page !== null}
                label="ui.exportCasesCsv"
                menu={{
                  label: p.text("ui.export"),
                  report: p.text("ui.caseReport"),
                  onReport: () => {
                    navigate(worklistPath(page.query, "/cases/report"));
                  },
                }}
              />
            }
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
        <WorklistFeedback page={page} action={primary} />
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
    </Page>
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
  const route = useContentRoute();
  const search = route.screen === "cases" ? route.query : (props.queryOverride ?? "");
  const admission = useMemo(() => parseWorklistQuery(search), [search]);
  const recovering = useRef(false);
  useEffect(() => {
    if (admission.kind === "invalid") {
      recovering.current = true;
    } else if (recovering.current && admission.selection.view !== undefined) {
      recovering.current = false;
      document.getElementById("case-list-title")?.focus();
    }
  }, [admission]);
  if (context === null) {
    return null;
  }
  if (admission.kind === "invalid") {
    return <CaseSelectionRecovery key={search} admission={admission} />;
  }
  const query = admission.selection;
  if (Object.keys(query).length === 0) {
    const cases = context.links.find(
      (named) => named.rel === "VIEW" && named.link.endpoint === apiEndpoints.caseList,
    );
    const view = cases === undefined ? undefined : caseLinkView(cases.link);
    if (view !== undefined) {
      return props.queryOverride === undefined ? (
        <DefaultCaseView query={query} view={view} />
      ) : (
        <ReadyCaseList {...props} query={{ ...query, view }} />
      );
    }
  }
  return <ReadyCaseList {...props} query={query} />;
};

/** List selection and editors belong to one authenticated session. */
export const CaseList = (props: Props) => {
  const p = usePresentation();
  useDocumentTitle(p.text("label.cases"));
  const { state } = useSessionContext();
  return state.kind === "authenticated" ? (
    <AvailableCaseList key={`${state.epoch}:${state.token}`} {...props} />
  ) : null;
};
