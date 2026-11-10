import { isDisclosureRefused } from "../api/outcomes";
import { ObservedReceipt } from "./operation/ObservedReceipt";
import { useWorkspaceMutationLock } from "../hooks/useWorkspaceMutationLock";
import { ScreenView } from "../ui/ScreenView";
import { SettingsWorkspace } from "./SettingsWorkspace";
import type { HumanReadDocument_context, TaskId } from "@contracts/client";
import {
  isActiveRequester,
  type ActiveRequester,
  RequesterContext,
} from "../hooks/useRequesterContext";
import type { SessionState } from "../session/useSession";
import { AccessState } from "./AccessState";
import { useCaseNavigation } from "../routing/useCaseNavigation";
import { WorkspaceViewsProvider } from "./WorkspaceViews";
import { RecoveryProvider } from "../recovery/RecoveryContext";
import "./workspace.css";
import { useSessionContext } from "../session/SessionContext";
import { useBusinessDate } from "../hooks/useBusinessDate";
import { RecoveryBanner } from "./RecoveryBanner";
import { WorkspaceHeader } from "./WorkspaceHeader";
import { RoutedContent } from "./RoutedContent";
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import type { CaseOperationReceipt, ServiceResponse } from "../api/service";
import type { Notice } from "../protocol/notices";
import { useEndpoint } from "../hooks/useEndpoint";
import { useEndpointCache } from "../hooks/EndpointProvider";
import { usePresentation } from "../presentation/context";
import { NoticeView, Message } from "../presentation/Message";
import { useRoute } from "../routing/useRoute";
import type { Route } from "../routing/routes";
import { casePath } from "../routing/routes";
import { navigate, takeRouteFocus } from "../routing/navigation";
const category = (
  route: Route,
): "cases" | "people" | "recovery" | "authorityHistory" | "settings" => {
  switch (route.screen) {
    case "people":
    case "recovery":
    case "authorityHistory":
    case "settings":
      return route.screen;
    default:
      return "cases";
  }
};
const availableRoute = (
  route: Route,
  views: ReadonlyArray<string>,
  tasks: ReadonlyArray<TaskId>,
) => {
  switch (category(route)) {
    case "settings":
    case "recovery":
      return true;
    case "people":
      return views.includes("AUTHORITY");
    case "authorityHistory":
      return tasks.includes("query:authorityHistory");
    case "cases":
      return views.includes("CASES");
  }
};
/** Only default entry chooses an area. Explicit deep links retain their URL and refusal state. */
const useInitialAllowedRoute = (route: Route, views: ReadonlyArray<string>, known: boolean) => {
  useEffect(() => {
    if (route.screen !== "home" || !known) {
      return;
    }
    let initial = "/settings";
    if (views.includes("AUTHORITY")) {
      initial = "/people";
    }
    if (views.includes("CASES")) {
      initial = "/cases";
    }
    navigate(initial, true);
  }, [route, views, known]);
};
const routeFocusTarget = (root: HTMLElement, id: string | null) => {
  const requested = id === null ? null : document.getElementById(id);
  const heading =
    [...root.querySelectorAll<HTMLElement>("h1")].find(
      (candidate) => candidate.closest("[hidden],[inert]") === null,
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
const useRouteHeading = (route: Route, owner: string | null) => {
  const identity = `${owner}:${route.screen === "cases" ? "cases" : JSON.stringify(route)}`;
  const content = useRef<HTMLDivElement>(null);
  const focused = useRef<string | null>(null);
  useEffect(() => {
    const target = content.current;
    if (target === null || focused.current === identity) {
      return;
    }
    const { id, scrollY } = takeRouteFocus() ?? { id: null, scrollY: null };
    const focus = () => {
      if (id !== null && target.querySelector('[aria-busy="true"]') !== null) {
        return false;
      }
      const destination = routeFocusTarget(target, id);
      if (destination.element === null) {
        return false;
      }
      focused.current = identity;
      moveRouteFocus(destination.element, destination.restored ? scrollY : null);
      return true;
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
      focused.current = identity;
      observer.disconnect();
      cancellation.abort();
    };
    const options = { capture: true, signal: cancellation.signal };
    document.addEventListener("pointerdown", interrupt, options);
    document.addEventListener("keydown", interrupt, options);
    observer.observe(target, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ["aria-busy"],
    });
    return () => {
      observer.disconnect();
      cancellation.abort();
    };
  }, [identity]);
  return content;
};
const WorkspaceStatus = ({ context }: { context: ReturnType<typeof useEndpoint<"context">> }) => {
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
const AcceptedToast = ({
  route,
  receipt,
  close,
}: {
  route: Route;
  receipt: { caseId: string; operationId: string } | null;
  close: () => void;
}) => {
  const p = usePresentation();
  if (
    receipt === null ||
    route.screen !== "case" ||
    route.target.kind !== "ID" ||
    route.target.caseId !== receipt.caseId
  ) {
    return null;
  }
  return (
    <div className="success-toast receipt">
      <ObservedReceipt key={receipt.operationId} operationId={receipt.operationId} />
      <button type="button" onClick={close}>
        {p.text("ui.close")}
      </button>
    </div>
  );
};
const succeededRequester = (response: ServiceResponse<"context"> | null) =>
  response?.outcome.kind === "SUCCEEDED" ? response.outcome.document : null;
const workspaceRequester = (context: ReturnType<typeof useEndpoint<"context">>) => {
  const observed = succeededRequester(context.value);
  const previous = succeededRequester(context.previous);
  const unreachable =
    context.result?.kind === "deliveryFailure" &&
    context.result.notice.kind === "local" &&
    context.result.notice.reason === "unreachable";
  const requester =
    observed ??
    ((context.loading || unreachable) && previous !== null && !isActiveRequester(previous)
      ? { ...previous, screen: { regions: [] } }
      : null);
  return requester;
};
const useWorkspaceAccess = (route: Route) => {
  const context = useEndpoint("context", {});
  const requester = workspaceRequester(context);
  const presentation = requester?.screen ?? null;
  const actorContext = requester !== null && isActiveRequester(requester) ? requester : null;
  const views = actorContext?.context.views ?? [];
  const active = category(route);
  const noViews = !context.loading && actorContext !== null && views.length === 0;
  useInitialAllowedRoute(route, views, actorContext !== null);
  return { context, requester, actorContext, presentation, views, active, noViews };
};
const WorkspaceUnavailable = ({ route }: { route: Route }) => {
  const p = usePresentation();
  let title = p.text("ui.cases");
  if (route.screen === "recovery") {
    title = p.text("ui.operationLookup");
  }
  if (route.screen === "case") {
    title = p.text("ui.caseDetail");
  } else if (route.screen === "authorityHistory") {
    title = p.text("ui.shell.authorityHistory");
  } else if (route.screen === "people") {
    title = p.text("ui.shell.people");
  }
  return (
    <section>
      <h1 tabIndex={-1}>{title}</h1>
      <p role="alert">
        <Message id="diagnostic.RESOURCE_UNAVAILABLE" />
      </p>
    </section>
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
  const [opened, setOpened] = useState(advertised);
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
      <h1 tabIndex={-1}>
        <Message id="ui.noViews" />
      </h1>
    );
  }
  return known ? fallback : null;
};
type WorkspaceRouteProps = Omit<Parameters<typeof RoutedContent>[0], "authorityAvailable"> & {
  views: ReadonlyArray<string>;
  noViews: boolean;
  tasks: ReadonlyArray<TaskId>;
  known: boolean;
};
const WorkspaceArea = ({ views, noViews, tasks, known, ...routing }: WorkspaceRouteProps) => {
  const active = category(routing.route);
  return (
    <WorkspaceContents
      key={active}
      advertised={routing.route.screen === "missing" || availableRoute(routing.route, views, tasks)}
      retainOperation={["people", "caseAct", "newCase"].includes(routing.route.screen)}
      noViews={routing.route.screen === "home" && noViews}
      known={known}
      fallback={<WorkspaceUnavailable route={routing.route} />}
    >
      <RoutedContent
        {...routing}
        authorityAvailable={views.includes("AUTHORITY")}
        caseAvailable={views.includes("CASES")}
      />
    </WorkspaceContents>
  );
};
const WorkspaceRoute = (props: WorkspaceRouteProps) => (
  <SettingsWorkspace route={props.route}>
    {(route) => <WorkspaceArea {...props} route={route} />}
  </SettingsWorkspace>
);
type WorkspaceOwner = {
  actorId: string;
  installationId: string;
  epoch: number;
  credential: string;
};
/** Feedback is a disclosure owned by this verified requester and session generation. */
const useAcceptanceFeedback = (
  owner: WorkspaceOwner | null,
  requester: HumanReadDocument_context | null,
  refused: boolean,
) => {
  const [accepted, setAccepted] = useState<{
    owner: object;
    receipt: { caseId: string; operationId: string };
  } | null>(null);
  const available =
    owner !== null &&
    requester !== null &&
    isActiveRequester(requester) &&
    requester.context.views.includes("CASES") &&
    requester.context.actorId === owner.actorId &&
    requester.context.installationId === owner.installationId;
  const denied = refused || (requester !== null && !available);
  // A missing read masks display without replacing the verified identity. Known denial does.
  const feedbackOwner = useMemo(
    () => (owner === null || denied ? null : { identity: owner }),
    [owner, denied],
  );
  const latestFeedbackOwner = useRef(feedbackOwner);
  useLayoutEffect(() => {
    latestFeedbackOwner.current = feedbackOwner;
    return () => {
      latestFeedbackOwner.current = null;
    };
  }, [feedbackOwner]);
  return {
    owner: feedbackOwner,
    receipt: available && accepted?.owner === feedbackOwner ? accepted.receipt : null,
    clear: () => {
      setAccepted(null);
    },
    accept: (receipt: CaseOperationReceipt, operationId: string) => {
      if (feedbackOwner === null || latestFeedbackOwner.current !== feedbackOwner) {
        return false;
      }
      setAccepted({
        owner: feedbackOwner,
        receipt: { caseId: receipt.context.target.caseId, operationId },
      });
      return true;
    },
  };
};
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
      owner.actorId !== requester.context.actorId ||
      owner.installationId !== requester.context.installationId ||
      owner.epoch !== session.epoch ||
      owner.credential !== session.token)
  ) {
    setOwner({
      actorId: requester.context.actorId,
      installationId: requester.context.installationId,
      epoch: session.epoch,
      credential: session.token,
    });
  }
  return { owner, session };
};
/** Retain local consent only for the same verified actor and authenticated session. */
const retainInactiveWorkspace = (
  requester: HumanReadDocument_context | null,
  owner: WorkspaceOwner | null,
  session: SessionState,
) => {
  if (requester === null || requester.context.state === "ACTIVE") {
    return true;
  }
  if (
    requester.context.state === "NOT_REGISTERED" ||
    owner === null ||
    session.kind !== "authenticated"
  ) {
    return false;
  }
  return (
    requester.context.actorId === owner.actorId &&
    session.epoch === owner.epoch &&
    session.token === owner.credential &&
    (requester.context.state !== "NO_ROLES" ||
      requester.context.installationId === owner.installationId)
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
  inactive: HumanReadDocument_context | null;
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
        {children}
      </div>
    ) : null}
  </>
);

const matchingLockOwner = (
  owner: WorkspaceOwner | null,
  session: SessionState,
  requester: HumanReadDocument_context | null,
): WorkspaceOwner | null => {
  if (
    owner === null ||
    session.kind !== "authenticated" ||
    owner.epoch !== session.epoch ||
    owner.credential !== session.token
  ) {
    return null;
  }
  if (requester === null) {
    return owner;
  }
  return requester.context.actorId === owner.actorId &&
    (requester.context.installationId === null ||
      requester.context.installationId === owner.installationId)
    ? owner
    : null;
};

export const Dashboard = () => {
  const route = useRoute();
  const { context, requester, actorContext, presentation, views, active, noViews } =
    useWorkspaceAccess(route);
  const cache = useEndpointCache();
  const { owner, session } = useVerifiedWorkspaceOwner(actorContext);
  const ownerIdentity = owner === null ? null : `${owner.installationId}:${owner.actorId}`;
  const lockOwner = matchingLockOwner(owner, session, requester);
  const feedback = useAcceptanceFeedback(
    lockOwner,
    requester,
    context.result !== null && isDisclosureRefused(context.result),
  );
  const caseNavigation = useCaseNavigation(feedback.owner);
  const { locked, change: setLocked } = useWorkspaceMutationLock(lockOwner);
  const [refresh, setRefresh] = useState(0);
  const content = useRouteHeading(route, ownerIdentity);
  const businessDate = useBusinessDate(actorContext);
  const tasks = actorContext?.context.tasks ?? [];
  const committed = (receipt: CaseOperationReceipt, operationId: string) => {
    if (!feedback.accept(receipt, operationId)) {
      return;
    }
    cache?.invalidate();
    setRefresh((value) => value + 1);
    navigate(casePath(receipt.context.target.caseId));
  };
  const inactive = requester !== null && requester.context.state !== "ACTIVE" ? requester : null;
  return (
    <RequesterContext value={actorContext}>
      <WorkspacePresence
        inactive={inactive}
        retained={retainInactiveWorkspace(requester, owner, session)}
        locked={locked}
        onCheck={context.reload}
        feedback={{ message: context.message, loading: context.loading }}
      >
        <RecoveryProvider>
          <WorkspaceViewsProvider>
            <div className="workspace-shell">
              <WorkspaceHeader
                route={route}
                presentation={presentation}
                openCase={caseNavigation.openCase}
                active={route.screen === "reminders" ? "reminders" : active}
                views={views}
                tasks={tasks}
                locked={locked}
              />
              <main className="app-shell">
                <AcceptedToast
                  route={route}
                  receipt={feedback.receipt}
                  close={() => {
                    feedback.clear();
                  }}
                />
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
                  <WorkspaceRoute
                    key={ownerIdentity}
                    route={route}
                    caseNavigation={caseNavigation}
                    views={views}
                    noViews={noViews}
                    tasks={tasks}
                    known={actorContext !== null}
                    setLocked={setLocked}
                    committed={committed}
                    refresh={refresh}
                  />
                </div>
              </main>
            </div>
          </WorkspaceViewsProvider>
        </RecoveryProvider>
      </WorkspacePresence>
    </RequesterContext>
  );
};
