import { PageHeading } from "../ui/Page";
import { CsvExportButton } from "../components/CsvExportButton";
import { apiEndpoints, type ServiceResponse } from "../api/service";
import { type BoundOperationAffordance, hasOperationBinding } from "../operation/affordances";
import { caseLinkTarget, caseLinkView, caseTargetPath } from "../routing/coreLinks";
import type { RegionRow, CaseSelector } from "@contracts/client";
import type { MutationLockChange } from "../hooks/useWorkspaceMutationLock";
import { PresentedValue } from "../components/PresentedValue";
import { useSessionContext } from "../session/SessionContext";
import { rememberWorklist } from "../routing/lastWorklist";
import { ListFooter } from "../ui/ListFooter";
import { ViewTabs } from "../ui/ViewTabs";
import { isTerm } from "../presentation/terms";
import {} from "./worklist/worklist.css";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { CaseReassignment, type ReassignmentSelection } from "./worklist/CaseReassignment";

import { useRequesterContext, useCaseCreationControl } from "../hooks/useRequesterContext";
import { operationTask } from "../protocol/taskCatalog";
import { renderCatalogKey } from "../presentation/messages";
import { followLink } from "../routing/followLink";
import { PageStatus } from "../components/PageStatus";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";

import { useEndpointPage } from "../hooks/useEndpointPage";
import { useFocusActivation } from "../routing/useRoute";
import { useContentRoute } from "../hooks/SettingsDetourContext";
import { navigate } from "../routing/navigation";
import {
  parseWorklistQuery,
  worklistPath,
  listRequest,
  type WorklistQuery,
} from "../routing/worklistQuery";
import { CaseSelectionRecovery } from "./worklist/CaseSelectionRecovery";
import { WorklistToolbar } from "./worklist/WorklistToolbar";
import { ResultPages } from "../ui/ResultPages";
import { ScreenView } from "../ui/ScreenView";
import { useWorkspaceViews, WorkspaceViewsStatus } from "./WorkspaceViews";
type Props = {
  onOpenCase?: () => void;
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
type Page = ReturnType<typeof useWorklist>;
const WorklistEmpty = ({ page, filtered }: { page: Page; filtered: boolean }) => {
  const p = usePresentation();
  const emptyId = page.page?.screen.regions.find(({ role }) => role === "RESULTS")?.emptyId;
  const emptyMessage = renderCatalogKey(p, emptyId ?? "label.noCasesForView");
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
          {page.items.length === 0 && customEmpty ? null : (
            <PageStatus
              empty={page.items.length === 0}
              cursor={page.cursor}
              emptyMessage="label.noCases"
            />
          )}
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
  const current = page.page;
  const exact = current?.exactTarget ?? null;
  const exactOrigin = "case-exact-match";
  return (
    <>
      {page.items.length === 0 && exact === null ? null : (
        <ResultPages
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
      {current === null || !current.screen.regions.some(({ role }) => role === "FACTS") ? null : (
        <ScreenView
          view={current.screen}
          timeZone={current.basis.timeZone}
          roles={["FACTS"]}
          {...(exact === null
            ? {}
            : {
                sectionLink: {
                  column: "exactReference",
                  id: exactOrigin,
                  href: caseTargetPath(exact),
                  follow: () => {
                    onSelect(exact, exactOrigin);
                  },
                },
              })}
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
const CaseListHeading = ({
  onOpenCase,
  available,
}: {
  onOpenCase: Props["onOpenCase"];
  available: boolean;
}) => {
  const p = usePresentation();
  const creation = useCaseCreationControl();
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
      primaryAction={
        creation === undefined || onOpenCase === undefined ? null : (
          <a
            id="open-case"
            className="ui-button ui-button--primary open-case-link"
            href="/cases/new"
            aria-disabled={!available || undefined}
            onClick={(event) => {
              followLink(event, () => {
                if (available) {
                  onOpenCase();
                }
              });
            }}
          >
            {renderCatalogKey(p, operationTask(creation.task).labelId)}
          </a>
        )
      }
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
  return (
    <section aria-labelledby="case-list-title" className="worklist" aria-busy={page.loading}>
      <CaseListHeading onOpenCase={onOpenCase} available={disclosureAvailable} />
      <WorkspaceViewsStatus area="cases" />

      <ViewTabs
        items={quickViews.map(({ view, label, count, prominence }) => ({
          id: view,
          prominence,
          menuLabel:
            label.kind === "MESSAGE" && count.kind === "COUNT"
              ? `${renderCatalogKey(p, label.messageId)} · ${p.integer(count.value)}`
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
  const route = useContentRoute();
  const search = route.screen === "cases" ? route.query : "";
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
      return <DefaultCaseView query={query} view={view} />;
    }
  }
  return <ReadyCaseList {...props} query={query} />;
};

/** List selection and editors belong to one authenticated session. */
export const CaseList = (props: Props) => {
  const { state } = useSessionContext();
  return state.kind === "authenticated" ? (
    <AvailableCaseList key={`${state.epoch}:${state.token}`} {...props} />
  ) : null;
};
