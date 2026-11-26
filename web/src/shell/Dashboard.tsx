import { useAcceptedOperation } from "../hooks/useAcceptedOperation";
import { Page } from "../ui/Page";
import { DocumentTitleScope, PhoneTitleTarget, useDocumentTitle } from "../hooks/useDocumentTitle";
import { PhoneActionTarget } from "../ui/PhoneAction";
import { hasStaticMessage } from "../presentation/messages";
import { apiEndpoints, type CaseLinkedReceipt, type ServiceResponse } from "../api/service";
import { hasDocumentSubject, type RequesterDocument } from "../protocol/types";
import { InlineNoteDraftProvider, InlineNoteRecovery } from "../features/case/InlineNoteDraft";
import { LinkNavigationContext } from "../routing/LinkNavigationContext";
import type { Link, TaskId, Region, Screen } from "@contracts/client";

import type { OwnerScope } from "../protocol/ownerScope";
import { isDisclosureRefused } from "../api/outcomes";
import { OperationFeedback, receiptOutcomeMessage } from "../features/operation/ReceiptFeedback";
import { useWorkspaceMutationLock } from "../hooks/useWorkspaceMutationLock";
import { ScreenView } from "../ui/ScreenView";
import { SettingsWorkspace } from "../features/settings/SettingsWorkspace";

import {
  isActiveRequester,
  type ActiveRequester,
  RequesterContext,
} from "../hooks/useRequesterContext";
import type { SessionState } from "../session/useSession";
import { AccessState } from "./AccessState";
import { useCaseNavigation } from "../routing/useCaseNavigation";
import { WorkspaceViewsProvider } from "../features/work/WorkspaceViews";
import { RecoveryProvider } from "../recovery/RecoveryContext";
import {} from "./workspace.css";
import { useSessionContext } from "../session/SessionContext";
import { useBusinessDate } from "../hooks/useBusinessDate";
import { RecoveryBanner } from "../features/recovery/RecoveryBanner";
import { isWorkspaceLinkId, Shell } from "./Shell";
import { RoutedContent } from "./RoutedContent";
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";

import type { Notice } from "../protocol/notices";
import { useEndpoint } from "../hooks/useEndpoint";
import { useEndpointCache } from "../hooks/EndpointProvider";
import { usePresentation } from "../presentation/context";
import { NoticeView, Message } from "../presentation/Message";
import { useFocusActivation, useRoute } from "../routing/useRoute";
import { type Route, casePath } from "../routing/routes";

import { navigate, takeRouteFocus, setRouteFocusOwner } from "../routing/navigation";
const feedbackDenied = (
  owner: unknown,
  requester: RequesterDocument | null,
  refused: boolean,
  routeAvailable: boolean,
) => owner === null || refused || (requester !== null && !routeAvailable);

const category = (
  route: Route,
): "cases" | "people" | "recovery" | "peopleHistory" | "disclosureTrail" | "settings" => {
  switch (route.screen) {
    case "people":
    case "recovery":
    case "peopleHistory":
    case "disclosureTrail":
    case "settings":
      return route.screen;
    default:
      return "cases";
  }
};
const availableRoute = (
  route: Route,
  views: ReadonlyArray<Link["endpoint"]>,
  tasks: ReadonlyArray<TaskId>,
) => {
  switch (category(route)) {
    case "settings":
    case "recovery":
      return true;
    case "people":
      return views.includes(apiEndpoints.peopleList);
    case "peopleHistory":
      return tasks.includes(apiEndpoints.peopleHistory);
    case "disclosureTrail":
      return views.includes(apiEndpoints.disclosureTrail);
    case "cases":
      return views.includes(apiEndpoints.caseList);
  }
};
/** Only default entry chooses an area. Explicit deep links retain their URL and refusal state. */
const useInitialAllowedRoute = (
  route: Route,
  views: ReadonlyArray<Link["endpoint"]>,
  known: boolean,
) => {
  useEffect(() => {
    if (route.screen !== "home" || !known) {
      return;
    }
    let initial = "/settings";
    if (views.includes(apiEndpoints.peopleList)) {
      initial = "/people";
    }
    if (views.includes(apiEndpoints.caseList)) {
      initial = "/cases";
    }
    navigate(initial, true);
  }, [route, views, known]);
};
const routeFocusTarget = (root: HTMLElement, id: string | null) => {
  const elementAtId = id === null ? null : document.getElementById(id);
  const requested =
    elementAtId?.closest('[hidden],[inert],[aria-hidden="true"]') === null ? elementAtId : null;
  const heading =
    [...root.querySelectorAll<HTMLElement>("h1")].find(
      (candidate) => candidate.closest('[hidden],[inert],[aria-hidden="true"]') === null,
    ) ?? null;
  const dialog = document.querySelector<HTMLElement>('[role="dialog"]');
  return { element: requested ?? dialog ?? heading, restored: requested !== null };
};
const moveRouteFocus = (element: HTMLElement, scrollY: number | null) => {
  const closed = element.closest<HTMLDetailsElement>("details:not([open])");
  if (closed !== null) {
    closed.open = true;
  }
  if (element.matches("h1,h2,h3")) {
    element.tabIndex = -1;
  }
  element.focus({ preventScroll: scrollY !== null });
  if (scrollY !== null) {
    window.scrollTo(0, scrollY);
  }
};
const useRouteHeading = (
  route: Route,
  owner: string | null,
  { loading, admitted }: { loading: boolean; admitted: boolean },
) => {
  const identity = `${owner}:${route.screen === "cases" ? "cases" : JSON.stringify(route)}`;
  const activation = useFocusActivation();
  const content = useRef<HTMLDivElement>(null);
  const focused = useRef<{
    identity: string;
    activation: number;
    id: string | null;
    scrollY: number | null;
    complete: boolean;
  } | null>(null);
  useEffect(() => {
    const target = content.current;
    if (target === null) {
      return;
    }
    const intent =
      focused.current?.identity === identity && focused.current.activation === activation.revision
        ? focused.current
        : {
            identity,
            activation: activation.revision,
            ...(takeRouteFocus() ?? { id: null, scrollY: null }),
            complete: false,
          };
    focused.current = intent;
    if (intent.complete) {
      return;
    }
    const { id, scrollY } = intent;
    const focus = () => {
      // A departing sheet or guard can still own focus during the route render.
      if (
        loading ||
        document.querySelector("[data-navigation-guard], .shell-more, .find-control") !== null
      ) {
        return false;
      }
      const { element, restored } = routeFocusTarget(target, id);
      const busy = '[aria-busy="true"]';
      // The shell renders all offered links as soon as the requester is admitted.
      const waitingForContent = id !== null && !restored && !(admitted && isWorkspaceLinkId(id));
      if (
        element === null ||
        (restored && element.closest(busy) !== null) ||
        (waitingForContent && target.querySelector(busy) !== null)
      ) {
        return false;
      }
      moveRouteFocus(element, restored ? scrollY : null);
      intent.complete = document.activeElement === element;
      return intent.complete;
    };
    if (focus()) {
      return;
    }
    const cancellation = new window.AbortController();
    const observer = new MutationObserver(() => {
      if (focus()) {
        observer.disconnect();
        cancellation.abort();
      }
    });
    const interrupt = () => {
      intent.complete = true;
      observer.disconnect();
      cancellation.abort();
    };
    const options = { capture: true, signal: cancellation.signal };
    document.addEventListener("pointerdown", interrupt, options);
    document.addEventListener("keydown", interrupt, options);
    observer.observe(document.body, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ["aria-busy", "aria-hidden", "hidden", "inert"],
    });
    return () => {
      observer.disconnect();
      cancellation.abort();
    };
  }, [identity, loading, admitted, activation.revision]);
  return content;
};
const WorkspaceStatus = ({
  context,
}: {
  context: ReturnType<typeof useEndpoint<typeof apiEndpoints.context>>;
}) => {
  const p = usePresentation();
  return (
    <>
      {context.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {context.message === null ? null : (
        <div role="alert">
          <NoticeView value={context.message} />
          <button type="button" onClick={context.reload}>
            {p.text("ui.tryAgain")}
          </button>
        </div>
      )}
    </>
  );
};
const succeededRequester = (response: ServiceResponse<typeof apiEndpoints.context> | null) =>
  response?.outcome.kind === "SUCCEEDED" &&
  hasDocumentSubject(response.outcome.document, "REQUESTER")
    ? response.outcome.document
    : null;
/** An unreachable refresh retains static Core guidance while removing caller facts and controls. */
const requesterGuidance = (requester: RequesterDocument): Screen => ({
  regions: requester.screen.regions.flatMap<Region>((region) => {
    if (
      region.role === "IDENTITY" &&
      region.titleId !== undefined &&
      hasStaticMessage(region.titleId)
    ) {
      return [
        { role: "IDENTITY", titleId: region.titleId, content: { kind: "ENTRIES", entries: [] } },
      ];
    }
    if (region.role === "NOTICE" && region.content.kind === "NOTICE") {
      const notices = region.content.notices
        .filter((item) => hasStaticMessage(item.messageId))
        .map(({ messageId, tone }) => ({ messageId, tone }));
      return notices.length === 0 ? [] : [{ role: "NOTICE", content: { kind: "NOTICE", notices } }];
    }
    return [];
  }),
});
const workspaceRequester = (
  context: ReturnType<typeof useEndpoint<typeof apiEndpoints.context>>,
) => {
  const observed = succeededRequester(context.value);
  const previous = succeededRequester(context.previous);
  const unreachable =
    context.result?.kind === "deliveryFailure" &&
    context.result.notice.kind === "local" &&
    context.result.notice.reason === "unreachable";
  const requester =
    observed ??
    ((context.loading || unreachable) && previous !== null && !isActiveRequester(previous)
      ? { ...previous, screen: requesterGuidance(previous), actions: [], links: [] }
      : null);
  return requester;
};
const useWorkspaceAccess = (route: Route) => {
  const context = useEndpoint(apiEndpoints.context, {});
  const requester = workspaceRequester(context);
  const presentation = requester?.screen ?? null;
  const actorContext = requester !== null && isActiveRequester(requester) ? requester : null;
  const views =
    actorContext?.links
      .filter((named) => named.rel === "VIEW")
      .map((named) => named.link.endpoint) ?? [];
  const active = category(route);
  const noViews = !context.loading && actorContext !== null && views.length === 0;
  useInitialAllowedRoute(route, views, actorContext !== null);
  return { context, requester, actorContext, presentation, views, active, noViews };
};
const WorkspaceUnavailable = ({ route }: { route: Route }) => {
  const p = usePresentation();
  let title = p.text("label.cases");
  if (route.screen === "recovery") {
    title = p.text("ui.operationLookup");
  }
  if (route.screen === "case") {
    title = p.text("label.caseDetail");
  } else if (route.screen === "peopleHistory") {
    title = p.text("endpoint.people.history.label");
  } else if (route.screen === "disclosureTrail") {
    title = p.text("endpoint.disclosure.trail.label");
  } else if (route.screen === "people") {
    title = p.text("view.PEOPLE.label");
  }
  useDocumentTitle(title);
  return (
    <Page id="workspace-unavailable-title" title={title}>
      <p role="alert">
        <Message id="diagnostic.RESOURCE_UNAVAILABLE" />
      </p>
    </Page>
  );
};
/** Visibility can change without destroying a scoped draft; verified identity changes replace its owner. */
const WorkspaceContents = ({
  advertised,
  retainOperation,
  noViews,
  known,
  fallback,
  children,
}: {
  advertised: boolean;
  retainOperation: boolean;
  noViews: boolean;
  known: boolean;
  fallback: ReactNode;
  children: ReactNode;
}) => {
  const p = usePresentation();
  const [opened, setOpened] = useState(advertised);
  useDocumentTitle(!advertised && !opened && noViews ? p.text("ui.noViews") : null);
  if (advertised && !opened) {
    setOpened(true);
  }
  if (advertised || (retainOperation && opened)) {
    return children;
  }
  if (opened) {
    return fallback;
  }
  if (noViews) {
    return (
      <Page id="workspace-no-views-title" title={p.text("ui.noViews")}>
        {null}
      </Page>
    );
  }
  return known ? fallback : null;
};
type WorkspaceRouteProps = Omit<Parameters<typeof RoutedContent>[0], "peopleAvailable"> & {
  views: ReadonlyArray<Link["endpoint"]>;
  noViews: boolean;
  tasks: ReadonlyArray<TaskId>;
  known: boolean;
  checking: boolean;
};
const WorkspaceArea = ({
  views,
  noViews,
  tasks,
  known,
  checking,
  ...routing
}: WorkspaceRouteProps) => {
  const active = category(routing.route);
  return (
    <WorkspaceContents
      key={active}
      advertised={routing.route.screen === "missing" || availableRoute(routing.route, views, tasks)}
      retainOperation={
        ["people", "caseAct", "newCase"].includes(routing.route.screen) ||
        (routing.route.screen === "case" && checking)
      }
      noViews={routing.route.screen === "home" && noViews}
      known={known}
      fallback={<WorkspaceUnavailable route={routing.route} />}
    >
      <RoutedContent
        {...routing}
        peopleAvailable={views.includes(apiEndpoints.peopleList)}
        caseAvailable={views.includes(apiEndpoints.caseList)}
        caseAuthoringAvailable={views.includes(apiEndpoints.caseList) || checking}
      />
    </WorkspaceContents>
  );
};
const WorkspaceRoute = (props: WorkspaceRouteProps) => (
  <SettingsWorkspace route={props.route}>
    {(route) => <WorkspaceArea {...props} route={route} />}
  </SettingsWorkspace>
);
type WorkspaceOwner = OwnerScope & { credential: string };
const BusinessDateNotice = ({ date, refresh }: { date: string; refresh: () => void }) => {
  const p = usePresentation();
  return (
    <aside className="shell-warning" role="status">
      <p>{p.text("ui.shell.businessDateChanged", { date: p.dateHeading(date) })}</p>
      <button type="button" onClick={refresh}>
        {p.text("ui.refreshWork")}
      </button>
    </aside>
  );
};

const useVerifiedWorkspaceOwner = (requester: ActiveRequester | null) => {
  const { state: session } = useSessionContext();
  const [owner, setOwner] = useState<WorkspaceOwner | null>(null);
  if (
    requester !== null &&
    session.kind === "authenticated" &&
    (owner === null ||
      owner.actorId !== requester.subject.actorId ||
      owner.installationId !== requester.subject.installationId ||
      owner.sessionEpoch !== session.epoch ||
      owner.credential !== session.token)
  ) {
    setOwner({
      serviceOrigin: location.origin,
      actorId: requester.subject.actorId,
      installationId: requester.subject.installationId,
      sessionEpoch: session.epoch,
      credential: session.token,
    });
  }
  return { owner, session };
};
/** Retain local consent only for the same verified actor and authenticated session. */
const retainInactiveWorkspace = (
  requester: RequesterDocument | null,
  owner: WorkspaceOwner | null,
  session: SessionState,
) => {
  if (requester === null || requester.subject.state === "ACTIVE") {
    return true;
  }
  if (
    requester.subject.state === "NOT_REGISTERED" ||
    owner === null ||
    session.kind !== "authenticated"
  ) {
    return false;
  }
  return (
    requester.subject.actorId === owner.actorId &&
    session.epoch === owner.sessionEpoch &&
    session.token === owner.credential &&
    (requester.subject.state !== "NO_ROLES" ||
      requester.subject.installationId === owner.installationId)
  );
};
const WorkspacePresence = ({
  inactive,
  retained,
  locked,
  onCheck,
  feedback,
  children,
}: {
  inactive: RequesterDocument | null;
  retained: boolean;
  locked: boolean;
  onCheck: () => void;
  feedback: { message: Notice | null; loading: boolean };
  children: ReactNode;
}) => (
  <>
    {inactive === null ? null : (
      <AccessState requester={inactive} locked={locked} onCheck={onCheck} feedback={feedback} />
    )}
    {retained ? (
      <div hidden={inactive !== null} inert={inactive !== null}>
        <DocumentTitleScope active={inactive === null}>{children}</DocumentTitleScope>
      </div>
    ) : null}
  </>
);

const matchingLockOwner = (
  owner: WorkspaceOwner | null,
  session: SessionState,
  requester: RequesterDocument | null,
): WorkspaceOwner | null => {
  if (
    owner === null ||
    session.kind !== "authenticated" ||
    owner.sessionEpoch !== session.epoch ||
    owner.credential !== session.token
  ) {
    return null;
  }
  if (requester === null) {
    return owner;
  }
  return requester.subject.actorId === owner.actorId &&
    (requester.subject.installationId === null ||
      requester.subject.installationId === owner.installationId)
    ? owner
    : null;
};

const requesterTasks = (requester: ActiveRequester | null): ReadonlyArray<TaskId> =>
  requester === null
    ? []
    : [
        ...requester.actions.map((action) => action.task),
        ...requester.links.map((named) => named.link.endpoint),
      ];
export const Dashboard = () => {
  const route = useRoute();
  const { context, requester, actorContext, presentation, views, active, noViews } =
    useWorkspaceAccess(route);
  const cache = useEndpointCache();
  const { owner, session } = useVerifiedWorkspaceOwner(actorContext);
  const ownerIdentity = owner === null ? null : `${owner.installationId}:${owner.actorId}`;
  const focusScope = ownerIdentity === null ? null : `${session.epoch}:${ownerIdentity}`;
  useLayoutEffect(() => {
    setRouteFocusOwner(focusScope);
    return () => {
      setRouteFocusOwner(null);
    };
  }, [focusScope]);
  const lockOwner = matchingLockOwner(owner, session, requester);
  const requesterRefused = context.result !== null && isDisclosureRefused(context.result);
  const feedback = useAcceptedOperation(
    requester,
    feedbackDenied(
      lockOwner,
      requester,
      requesterRefused,
      availableRoute(route, views, requesterTasks(actorContext)),
    ),
  );
  const caseNavigation = useCaseNavigation(feedback.owner);
  const { locked, change: setLocked } = useWorkspaceMutationLock(lockOwner);
  const [refresh, setRefresh] = useState(0);
  const [phoneTitle, setPhoneTitle] = useState<{ path: string; title: string } | null>(null);
  const publishPhoneTitle = useCallback((title: string) => {
    const path = location.pathname + location.search;
    setPhoneTitle((previous) =>
      previous?.path === path && previous.title === title ? previous : { path, title },
    );
  }, []);
  const [phoneAction, setPhoneAction] = useState<HTMLDivElement | null>(null);
  const content = useRouteHeading(route, focusScope, {
    loading: context.loading,
    admitted: actorContext !== null,
  });
  const businessDate = useBusinessDate(actorContext);
  const tasks = requesterTasks(actorContext);
  const committed = (receipt: CaseLinkedReceipt, operationId: string) => {
    if (
      !feedback.accept(
        operationId,
        receiptOutcomeMessage(receipt.document),
        casePath(receipt.caseId),
      )
    ) {
      return;
    }
    if (receipt.document.disclosure === "ERASED") {
      cache?.observeAttachmentErasure(receipt.caseId);
    } else {
      cache?.invalidate();
    }
    setRefresh((value) => value + 1);
    navigate(casePath(receipt.caseId));
  };
  const inactive = requester !== null && requester.subject.state !== "ACTIVE" ? requester : null;
  return (
    <InlineNoteDraftProvider owner={lockOwner}>
      <RequesterContext value={requester}>
        <WorkspacePresence
          inactive={inactive}
          retained={retainInactiveWorkspace(requester, owner, session)}
          locked={locked}
          onCheck={context.reload}
          feedback={{ message: context.message, loading: context.loading }}
        >
          <RecoveryProvider identity={requesterRefused ? null : lockOwner}>
            <InlineNoteRecovery />
            <WorkspaceViewsProvider>
              <div className="workspace-shell">
                <Shell
                  route={route}
                  presentation={presentation}
                  active={route.screen === "reminders" ? "reminders" : active}
                  views={views}
                  tasks={tasks}
                  locked={locked}
                  phoneActionRef={setPhoneAction}
                  phoneTitle={
                    phoneTitle?.path === location.pathname + location.search
                      ? phoneTitle.title
                      : undefined
                  }
                />
                <PhoneTitleTarget value={publishPhoneTitle}>
                  <PhoneActionTarget value={phoneAction}>
                    <OperationFeedback feedback={feedback}>
                      <main className="workspace-main">
                        <RecoveryBanner />
                        {businessDate.changedDate === null ? null : (
                          <BusinessDateNotice
                            date={businessDate.changedDate}
                            refresh={businessDate.refresh}
                          />
                        )}
                        <WorkspaceStatus context={context} />
                        {presentation === null ? null : (
                          <ScreenView view={presentation} roles={["NOTICE"]} />
                        )}
                        <div ref={content}>
                          <LinkNavigationContext value={caseNavigation.linkNavigation}>
                            <WorkspaceRoute
                              key={ownerIdentity}
                              route={route}
                              caseNavigation={caseNavigation}
                              views={views}
                              noViews={noViews}
                              tasks={tasks}
                              known={actorContext !== null}
                              checking={context.loading}
                              setLocked={setLocked}
                              committed={committed}
                              refresh={refresh}
                            />
                          </LinkNavigationContext>
                        </div>
                      </main>
                    </OperationFeedback>
                  </PhoneActionTarget>
                </PhoneTitleTarget>
              </div>
            </WorkspaceViewsProvider>
          </RecoveryProvider>
        </WorkspacePresence>
      </RequesterContext>
    </InlineNoteDraftProvider>
  );
};
