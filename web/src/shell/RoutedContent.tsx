import { DocumentTitleScope, useDocumentTitle } from "../hooks/useDocumentTitle";
import { WorkDialog } from "../ui/WorkDialog";
import { Button } from "../ui/Button";
import { DataTable } from "../ui/DataTable";
import { SubjectHeader } from "../ui/SubjectHeader";
import { renderCatalogKey } from "../presentation/messages";
import "../features/case/entry-selection.css";
import { operationTask } from "../protocol/taskCatalog";
import type { ServiceResponse, CurrentCase, CaseLinkedReceipt } from "../api/service";
import { useCaseCreationControl } from "../hooks/useRequesterContext";
import { hasDocumentSubject } from "../protocol/types";
import { useEndpointPage } from "../hooks/useEndpointPage";
import type { Affordance, CaseSelector, EndpointId, RequestByEndpoint } from "@contracts/client";

import {
  allActions,
  isOperationAffordance,
  type OperationAffordance,
} from "../operation/affordances";
import type { MutationLockChange } from "../hooks/useWorkspaceMutationLock";
import type { PeopleHistory as PeopleHistoryType } from "../features/people/PeopleHistory";
import { SettingsPage } from "../features/settings/SettingsPage";
import type { useCaseNavigation } from "../routing/useCaseNavigation";
import { useEffect, useState, useCallback, type ComponentProps } from "react";
import { ViewModule } from "./ViewModule";

import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import { productMetadata } from "../protocol/productMetadata";
import { isCaseAct, type ActKind } from "../operation/metadata";
import { type Route } from "../routing/routes";
import type { PeoplePanel as PeoplePanelType } from "../features/people/PeoplePanel";
import type { CaseDetail as CaseDetailType } from "../features/case/CaseDetail";
import { ReminderQueue } from "../features/work/ReminderQueue";
import { CaseReport } from "../features/work/worklist/SummaryBand";
import { CaseList } from "../features/work/CaseList";
import { MissingPage } from "./MissingPage";
import type { OperationEditor as OperationEditorType } from "../features/operation/OperationEditor";

const loadDisclosureTrail = () =>
  import("../features/people/DisclosureTrail").then((module) => module.DisclosureTrail);
const DisclosureTrail = () => <ViewModule load={loadDisclosureTrail} viewProps={{}} />;

const loadPeopleHistory = () =>
  import("../features/people/PeopleHistory").then((module) => module.PeopleHistory);
const PeopleHistory = (props: ComponentProps<typeof PeopleHistoryType>) => (
  <ViewModule load={loadPeopleHistory} viewProps={props} />
);
const loadPeoplePanel = () =>
  import("../features/people/PeoplePanel").then((module) => module.PeoplePanel);
const PeoplePanel = (props: ComponentProps<typeof PeoplePanelType>) => (
  <ViewModule load={loadPeoplePanel} viewProps={props} />
);
const loadCaseDetail = () =>
  import("../features/case/CaseDetail").then((module) => module.CaseDetail);
const CaseDetail = (props: ComponentProps<typeof CaseDetailType>) => (
  <ViewModule load={loadCaseDetail} viewProps={props} />
);
const loadOperationEditor = () =>
  import("../features/operation/OperationEditor").then((module) => module.OperationEditor);
const OperationEditor = (props: ComponentProps<typeof OperationEditorType>) => (
  <ViewModule load={loadOperationEditor} viewProps={props} />
);
const loadRecoveryPanel = () =>
  import("../features/recovery/RecoveryPanel").then((module) => module.RecoveryPanel);
const RecoveryPanel = ({ operationId }: { operationId: string }) => (
  <ViewModule
    load={loadRecoveryPanel}
    viewProps={operationId === "" ? {} : { initialOperationId: operationId }}
  />
);

export type EditorCallbacks = {
  setLocked: MutationLockChange;
  committed: (receipt: CaseLinkedReceipt, operationId: string) => void;
};
type EditorRoute = Extract<Route, { screen: "caseAct" }>;
type EditorSelectionProps = {
  current: CurrentCase;
  route: EditorRoute;
  onReady: (value: { current: CurrentCase; action: OperationAffordance }) => void;
  onClose: () => void;
  onUnavailable: () => void;
};
const matchesEditorAction = (
  action: Affordance,
  route: EditorRoute,
): action is OperationAffordance => {
  if (action.task !== route.act || !isOperationAffordance(action) || action.blocked !== null) {
    return false;
  }
  const binding: Readonly<Record<string, unknown>> = action.binding;
  return Object.entries(route.binding ?? {}).every(
    ([key, value]) =>
      Object.hasOwn(binding, key) && JSON.stringify(binding[key]) === JSON.stringify(value),
  );
};
type EntrySelectionSource = {
  [K in EndpointId]: RequestByEndpoint[K] extends { target: CaseSelector }
    ? "cursor" extends keyof RequestByEndpoint[K]
      ? K
      : never
    : never;
}[EndpointId];
type EntrySelectionDeclaration = { readonly promptId: string; readonly from: EntrySelectionSource };

const entrySelectionPage = (reply: ServiceResponse<EntrySelectionSource>) => {
  if (reply.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const { document } = reply.outcome;
  if (!hasDocumentSubject(document, "CASE") || document.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    document,
    items: document.screen.regions.flatMap(({ content }) =>
      content.kind === "TABLE" ? content.rows.flatMap((row) => row.actions ?? []) : [],
    ),
    nextCursor: document.nextCursor,
    basis: document.basis.observation,
    screen: document.screen,
    disclosure: document.disclosure,
  };
};
const entrySelectionKey = (page: NonNullable<ReturnType<typeof entrySelectionPage>>) =>
  JSON.stringify(page.document.subject);
type EntryPage = NonNullable<ReturnType<typeof entrySelectionPage>>;
type EntryQuery = ReturnType<typeof useEndpointPage<EntrySelectionSource, Affordance, EntryPage>>;

const entrySelection = (page: EntryQuery, current: CurrentCase, route: EditorRoute) => {
  const exact = route.binding !== undefined;
  const matching = page.items.filter((candidate): candidate is OperationAffordance =>
    matchesEditorAction(candidate, route),
  );
  const outdated =
    page.page !== null && page.page.document.subject.revision !== current.subject.revision;
  return {
    exact,
    action: exact ? matching[0] : undefined,
    outdated,
    unavailable:
      !page.loading && page.cursor === null && matching.length === 0 && page.message === null,
  };
};

const focusSelectionPrompt = (element: HTMLParagraphElement | null) => {
  if (element !== null) {
    requestAnimationFrame(() => {
      if (element.isConnected) {
        element.focus();
      }
    });
  }
};

const EntryChoiceRows = ({
  page,
  route,
  current,
  onReady,
}: Pick<EditorSelectionProps, "route" | "current" | "onReady"> & {
  page: EntryPage | null;
}) => {
  const p = usePresentation();
  return page?.screen.regions.map(({ role, content }) => {
    if (content.kind !== "TABLE") {
      return null;
    }
    const rows = content.rows.flatMap((row) => {
      const offered = row.actions?.find((candidate) => matchesEditorAction(candidate, route));
      return offered === undefined ? [] : [{ ...row, actions: [offered] }];
    });
    return rows.length === 0 ? null : (
      <div key={role} className="entry-choices">
        <DataTable
          view={{ kind: "TABLE", columns: content.columns, rows }}
          timeZone={page.basis.timeZone}
          renderControls={(row) => {
            const offered = row.actions?.[0];
            return offered !== undefined && matchesEditorAction(offered, route) ? (
              <Button
                className="data-table-recommended"
                variant="secondary"
                compact
                onPress={() => {
                  onReady({ current, action: offered });
                }}
              >
                {renderCatalogKey(p, offered.shortLabelId ?? offered.labelId)}
              </Button>
            ) : null;
          }}
        />
      </div>
    );
  });
};

export const EntrySelection = ({
  current,
  route,
  onReady,
  onClose,
  onUnavailable,
  revalidate,
  declaration,
}: EditorSelectionProps & { revalidate: () => void; declaration: EntrySelectionDeclaration }) => {
  const p = usePresentation();
  const input = useCallback(
    (cursor: string | null) => ({
      target: route.target,
      ...(cursor === null ? {} : { cursor }),
    }),
    [route.target],
  );
  const page = useEndpointPage<
    EntrySelectionSource,
    Affordance,
    NonNullable<ReturnType<typeof entrySelectionPage>>
  >(declaration.from, input, entrySelectionPage, revalidate, undefined, entrySelectionKey);
  const { exact, action, outdated, unavailable } = entrySelection(page, current, route);
  useEffect(() => {
    if (outdated) {
      revalidate();
    } else if (action !== undefined) {
      onReady({ current, action });
    } else if (unavailable) {
      onUnavailable();
    } else if (exact && !page.loading && page.message === null && page.cursor !== null) {
      void page.load(page.cursor);
    }
  }, [outdated, action, current, onReady, onUnavailable, revalidate, exact, page, unavailable]);
  if (unavailable) {
    return null;
  }
  const loading = page.loading || exact;
  return (
    <WorkDialog
      title={p.actLabel(route.act)}
      closeLabel={p.text("ui.close")}
      isOpen
      isDismissable
      identity={
        <SubjectHeader
          region={current.screen.regions.find(({ role }) => role === "IDENTITY")}
          timeZone={current.basis.observation.timeZone}
          renderTitle={(reference) => <h3 className="case-context-title">{reference}</h3>}
        />
      }
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
    >
      {exact ? null : (
        <p tabIndex={-1} ref={focusSelectionPrompt}>
          {renderCatalogKey(p, declaration.promptId)}
        </p>
      )}
      {page.message === null ? null : (
        <>
          <p role="alert">
            <NoticeView value={page.message} />
          </p>
          <Button
            variant="secondary"
            onPress={() => {
              void page.load(null);
            }}
          >
            {p.text("ui.tryAgain")}
          </Button>
        </>
      )}
      {loading ? <p role="status">{p.text("ui.loadingCurrentCase")}</p> : null}
      <EntryChoiceRows
        page={outdated || exact ? null : page.page}
        route={route}
        current={current}
        onReady={onReady}
      />
      {page.cursor === null || exact ? null : (
        <Button
          variant="secondary"
          onPress={() => {
            void page.load(page.cursor);
          }}
        >
          {p.text("ui.nextPage")}
        </Button>
      )}
    </WorkDialog>
  );
};

const RoutableCaseEditor = ({
  route,
  current,
  revalidate,
  onClose,
  onUnavailable,
  onObserved,
  disclosureAvailable,
  committed,
  setLocked,
}: EditorCallbacks & {
  route: EditorRoute & { act: ActKind };
  current: CurrentCase;
  revalidate: () => void;
  onClose: () => void;
  onUnavailable: () => void;
  onObserved: (value: CurrentCase) => void;
  disclosureAvailable: boolean;
}) => {
  const [opened, setOpened] = useState<{
    current: CurrentCase;
    action: OperationAffordance;
  } | null>(null);
  const definition = operationTask(route.act);
  const candidates = allActions(current);
  const action = candidates.find((candidate): candidate is OperationAffordance =>
    matchesEditorAction(candidate, route),
  );
  const direct = definition.selection === null || route.binding !== undefined;
  const selection = opened ?? (direct && action !== undefined ? { current, action } : null);
  if (opened === null && selection !== null) {
    setOpened(selection);
  }
  const unavailable = selection === null && definition.selection === null;
  useEffect(() => {
    if (unavailable) {
      onUnavailable();
    }
  }, [onUnavailable, unavailable]);
  if (selection === null) {
    return definition.selection === null ? null : (
      <EntrySelection
        declaration={definition.selection}
        route={route}
        current={current}
        onReady={setOpened}
        onClose={onClose}
        onUnavailable={onUnavailable}
        revalidate={revalidate}
      />
    );
  }
  return (
    <OperationEditor
      definition={productMetadata}
      current={selection.current}
      initialAct={route.act}
      affordance={selection.action}
      {...(selection.action.binding.kind === "CASE" ? { binding: selection.action.binding } : {})}
      autoReview={Object.keys(route.binding ?? {}).some((key) => key !== "kind")}
      disclosureAvailable={disclosureAvailable}
      onCaseObserved={onObserved}
      onClose={onClose}
      onCommitted={committed}
      onMutationLockChange={setLocked}
    />
  );
};

const NewCaseEditor = ({
  setLocked,
  committed,
  disclosureAvailable,
  onClose,
}: EditorCallbacks & { disclosureAvailable: boolean; onClose: () => void }) => {
  const p = usePresentation();
  const action = useCaseCreationControl();
  const [initial, setInitial] = useState<ActKind | null>(null);
  if (initial === null && action !== undefined && isCaseAct(action.task)) {
    setInitial(action.task);
  }
  useDocumentTitle(initial === null ? null : p.actLabel(initial));
  return initial === null ? (
    <MissingPage inWorkspace />
  ) : (
    <OperationEditor
      definition={productMetadata}
      current={null}
      initialAct={initial}
      disclosureAvailable={disclosureAvailable && action?.task === initial}
      onClose={onClose}
      onCommitted={committed}
      onMutationLockChange={setLocked}
    />
  );
};

const AreaContent = ({
  route,
  setLocked,
  caseAvailable,
}: {
  route: Route;
  setLocked: MutationLockChange;
  caseAvailable: boolean;
}) => {
  switch (route.screen) {
    case "settings":
      return <SettingsPage />;
    case "reminders":
      return <ReminderQueue onLockChange={setLocked} disclosureAvailable={caseAvailable} />;
    case "recovery":
      return <RecoveryPanel key={route.operationId} operationId={route.operationId} />;
    case "disclosureTrail":
      return <DisclosureTrail />;
    case "peopleHistory":
      return <PeopleHistory key={route.actorId} actorId={route.actorId} />;
    case "missing":
      return <MissingPage inWorkspace />;
    default:
      return null;
  }
};
type CaseRouteProps = EditorCallbacks & {
  caseNavigation: ReturnType<typeof useCaseNavigation>;
  caseAvailable: boolean;
};
const CaseListRoute = ({
  creating,
  caseNavigation,
  caseAvailable,
  setLocked,
  committed,
}: CaseRouteProps & { creating: boolean }) => (
  <>
    <DocumentTitleScope active={!creating}>
      <CaseList
        onSelect={caseNavigation.selectCase}
        onOpenCase={caseNavigation.openCase}
        onMutationLockChange={setLocked}
        disclosureAvailable={caseAvailable}
        queryOverride={creating ? caseNavigation.openCaseQuery : undefined}
      />
    </DocumentTitleScope>
    {creating ? (
      <NewCaseEditor
        onClose={caseNavigation.backToCases}
        setLocked={setLocked}
        committed={committed}
        disclosureAvailable={caseAvailable}
      />
    ) : null}
  </>
);
const CaseRoute = ({
  route,
  caseNavigation,
  caseAvailable,
  caseAuthoringAvailable,
  refresh,
  onFind,
  setLocked,
  committed,
}: CaseRouteProps & {
  route: Extract<Route, { screen: "case" | "caseAct" }>;
  refresh: number;
  onFind: ComponentProps<typeof CaseDetailType>["onFind"];
  caseAuthoringAvailable: boolean;
}) => {
  const act = route.screen === "caseAct" ? route.act : null;
  if (act !== null && !isCaseAct(act)) {
    return <MissingPage inWorkspace />;
  }
  const operationKey = JSON.stringify(route);
  const operation: ComponentProps<typeof CaseDetailType>["operation"] =
    route.screen === "caseAct" && act !== null
      ? {
          key: operationKey,
          act,
          render: (current, revalidate, onUnavailable, available, onObserved) => (
            <RoutableCaseEditor
              key={operationKey}
              route={{ ...route, act }}
              current={current}
              revalidate={revalidate}
              onUnavailable={onUnavailable}
              onObserved={onObserved}
              onClose={() => {
                caseNavigation.closeAct(route.target);
              }}
              disclosureAvailable={available}
              setLocked={setLocked}
              committed={committed}
            />
          ),
        }
      : undefined;
  return (
    <CaseDetail
      target={route.target}
      reloadSignal={refresh}
      disclosureAvailable={caseAvailable}
      authoringAvailable={caseAuthoringAvailable}
      definition={productMetadata.catalog}
      onBack={caseNavigation.backToCases}
      onFind={onFind}
      onAct={caseNavigation.openAct}
      onMutationLockChange={setLocked}
      operation={operation}
    />
  );
};
export const RoutedContent = ({
  route,
  setLocked,
  committed,
  refresh,
  peopleAvailable = true,
  caseAvailable = true,
  caseAuthoringAvailable = caseAvailable,
  caseNavigation,
  onFind,
}: EditorCallbacks & {
  route: Route;
  refresh: number;
  peopleAvailable?: boolean;
  caseAvailable?: boolean;
  caseAuthoringAvailable?: boolean;
  onFind?: ComponentProps<typeof CaseDetailType>["onFind"];
  caseNavigation: ReturnType<typeof useCaseNavigation>;
}) => {
  const callbacks = { setLocked, committed, caseAvailable, caseNavigation };
  switch (route.screen) {
    case "caseReport":
      return <CaseReport query={route.query} available={caseAvailable} />;
    case "cases":
    case "newCase":
      return <CaseListRoute creating={route.screen === "newCase"} {...callbacks} />;
    case "case":
    case "caseAct":
      return (
        <CaseRoute
          route={route}
          refresh={refresh}
          onFind={onFind}
          caseAuthoringAvailable={caseAuthoringAvailable}
          {...callbacks}
        />
      );
    case "people":
      return <PeoplePanel onLockChange={setLocked} disclosureAvailable={peopleAvailable} />;
    default:
      return <AreaContent route={route} setLocked={setLocked} caseAvailable={caseAvailable} />;
  }
};
