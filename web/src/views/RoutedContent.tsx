import { DocumentTitleScope, useDocumentTitle } from "../hooks/useDocumentTitle";
import { Drawer } from "../ui/Drawer";
import { Button } from "../ui/Button";
import { DataTable } from "../ui/DataTable";
import { SubjectHeader } from "../ui/SubjectHeader";
import { renderCatalogKey } from "../presentation/messages";
import "./case/payment-selection.css";
import { operationTask } from "../protocol/taskCatalog";
import {
  apiEndpoints,
  type ServiceResponse,
  type CurrentCase,
  type CaseLinkedReceipt,
} from "../api/service";
import { useCaseCreationControl } from "../hooks/useRequesterContext";
import { hasDocumentSubject } from "../protocol/types";
import { useEndpointPage } from "../hooks/useEndpointPage";
import type { Affordance } from "@contracts/client";

import {
  allActions,
  isOperationAffordance,
  type OperationAffordance,
} from "../operation/affordances";
import type { MutationLockChange } from "../hooks/useWorkspaceMutationLock";
import type { PeopleHistory as PeopleHistoryType } from "./people/PeopleHistory";
import { SettingsPage } from "../features/settings/SettingsPage";
import type { useCaseNavigation } from "../routing/useCaseNavigation";
import { useEffect, useState, useCallback, type ComponentProps } from "react";
import { ViewModule } from "./ViewModule";

import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import { productMetadata } from "../protocol/productMetadata";
import { isCaseAct, type ActKind } from "../operation/metadata";
import { type Route } from "../routing/routes";
import type { PeoplePanel as PeoplePanelType } from "./PeoplePanel";
import type { CaseDetail as CaseDetailType } from "./CaseDetail";
import { ReminderQueue } from "./work/ReminderQueue";
import { CaseReport } from "./worklist/SummaryBand";
import { CaseList } from "./CaseList";
import { MissingPage } from "./MissingPage";
import type { OperationEditor as OperationEditorType } from "./OperationEditor";

const loadDisclosureTrail = () =>
  import("./DisclosureTrail").then((module) => module.DisclosureTrail);
const DisclosureTrail = () => <ViewModule load={loadDisclosureTrail} viewProps={{}} />;

const loadPeopleHistory = () =>
  import("./people/PeopleHistory").then((module) => module.PeopleHistory);
const PeopleHistory = (props: ComponentProps<typeof PeopleHistoryType>) => (
  <ViewModule load={loadPeopleHistory} viewProps={props} />
);
const loadPeoplePanel = () => import("./PeoplePanel").then((module) => module.PeoplePanel);
const PeoplePanel = (props: ComponentProps<typeof PeoplePanelType>) => (
  <ViewModule load={loadPeoplePanel} viewProps={props} />
);
const loadCaseDetail = () => import("./CaseDetail").then((module) => module.CaseDetail);
const CaseDetail = (props: ComponentProps<typeof CaseDetailType>) => (
  <ViewModule load={loadCaseDetail} viewProps={props} />
);
const loadOperationEditor = () =>
  import("./OperationEditor").then((module) => module.OperationEditor);
const OperationEditor = (props: ComponentProps<typeof OperationEditorType>) => (
  <ViewModule load={loadOperationEditor} viewProps={props} />
);
const loadRecoveryPanel = () => import("./RecoveryPanel").then((module) => module.RecoveryPanel);
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
const paymentSelectionPage = (reply: ServiceResponse<typeof apiEndpoints.casePayments>) => {
  if (reply.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const { document } = reply.outcome;
  if (!hasDocumentSubject(document, "CASE") || document.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    document,
    items: allActions(document),
    nextCursor: document.nextCursor,
    basis: document.basis.observation,
    screen: document.screen,
    disclosure: document.disclosure,
  };
};
const paymentSelectionKey = (page: NonNullable<ReturnType<typeof paymentSelectionPage>>) =>
  JSON.stringify(page.document.subject);
type PaymentPage = NonNullable<ReturnType<typeof paymentSelectionPage>>;
type PaymentQuery = ReturnType<
  typeof useEndpointPage<typeof apiEndpoints.casePayments, Affordance, PaymentPage>
>;

const paymentSelection = (page: PaymentQuery, current: CurrentCase, route: EditorRoute) => {
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

const PaymentChoiceRows = ({
  payment,
  route,
  current,
  onReady,
}: Pick<EditorSelectionProps, "route" | "current" | "onReady"> & {
  payment: PaymentPage | null;
}) => {
  const p = usePresentation();
  const table = payment?.screen.regions.find(({ role }) => role === "PAYMENTS")?.content;
  const rows =
    table?.kind === "TABLE"
      ? table.rows.flatMap((row) => {
          const offered = row.actions?.find((candidate) => matchesEditorAction(candidate, route));
          return offered === undefined ? [] : [{ ...row, actions: [offered] }];
        })
      : [];
  return payment === null || table?.kind !== "TABLE" || rows.length === 0 ? null : (
    <div className="payment-entry-choices">
      <DataTable
        view={{ kind: "TABLE", columns: table.columns, rows }}
        timeZone={payment.basis.timeZone}
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
};

const PaymentEditorSelection = ({
  current,
  route,
  onReady,
  onClose,
  onUnavailable,
  revalidate,
  promptId,
}: EditorSelectionProps & { revalidate: () => void; promptId: string }) => {
  const p = usePresentation();
  const input = useCallback(
    (cursor: string | null) => ({
      target: route.target,
      limit: 50,
      ...(cursor === null ? {} : { cursor }),
    }),
    [route.target],
  );
  const page = useEndpointPage<
    typeof apiEndpoints.casePayments,
    Affordance,
    NonNullable<ReturnType<typeof paymentSelectionPage>>
  >(
    apiEndpoints.casePayments,
    input,
    paymentSelectionPage,
    revalidate,
    undefined,
    paymentSelectionKey,
  );
  const { exact, action, outdated, unavailable } = paymentSelection(page, current, route);
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
    <Drawer
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
          {renderCatalogKey(p, promptId)}
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
      <PaymentChoiceRows
        payment={outdated || exact ? null : page.page}
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
    </Drawer>
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
  const direct = definition.binding.identity === null || route.binding !== undefined;
  const selection = opened ?? (direct && action !== undefined ? { current, action } : null);
  if (opened === null && selection !== null) {
    setOpened(selection);
  }
  const unavailable = selection === null && definition.binding.identity === null;
  useEffect(() => {
    if (unavailable) {
      onUnavailable();
    }
  }, [onUnavailable, unavailable]);
  if (selection === null) {
    return definition.selection === null ? null : (
      <PaymentEditorSelection
        promptId={definition.selection.promptId}
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
  setLocked,
  committed,
}: CaseRouteProps & {
  route: Extract<Route, { screen: "case" | "caseAct" }>;
  refresh: number;
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
}: EditorCallbacks & {
  route: Route;
  refresh: number;
  peopleAvailable?: boolean;
  caseAvailable?: boolean;
  caseAuthoringAvailable?: boolean;
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
