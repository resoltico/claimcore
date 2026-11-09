import { ScreenView } from "../ui/ScreenView";
import { SettingsWorkspace } from "./SettingsWorkspace";
import type { RequesterState, RequesterState_ACTIVE } from "@contracts/client";
import type { SessionState } from "../session/useSession";
import { AccessState } from "./AccessState";
import { useCaseNavigation } from "../routing/useCaseNavigation";
import { WorkspaceViewsProvider } from "./WorkspaceViews";
import { MaintenanceNotice } from "../components/MaintenanceNotice";
import { RecoveryProvider } from "../recovery/RecoveryContext";
import "./workspace.css";
import { useSessionContext } from "../session/SessionContext";
import { RequesterContext } from "../hooks/useRequesterContext";
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
import { navigate, setNavigationLock, takeRouteFocus } from "../routing/navigation";
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
  capabilities: ReadonlyArray<string>,
) => {
  switch (category(route)) {
    case "settings":
    case "recovery":
      return true;
    case "people":
      return views.includes("AUTHORITY");
    case "authorityHistory":
      return capabilities.includes("READ_AUTHORITY");
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
  receipt: CaseOperationReceipt | null;
  close: () => void;
}) => {
  const p = usePresentation();
  if (
    receipt === null ||
    route.screen !== "case" ||
    (route.target.kind === "ID"
      ? route.target.caseId !== receipt.subject.caseId
      : route.target.caseReference !== receipt.subject.caseReference)
  ) {
    return null;
  }
  return (
    <div className="success-toast receipt">
      <ScreenView view={receipt.view} roles={["NOTICE"]} timeZone={receipt.basis.timeZone} />
      <button type="button" onClick={close}>
        {p.text("ui.close")}
      </button>
    </div>
  );
};
const useNavigationLock = (locked: boolean) => {
  useLayoutEffect(() => {
    setNavigationLock(locked);
    return () => {
      setNavigationLock(false);
    };
  }, [locked]);
};
const succeededRequester = (response: ServiceResponse<"context"> | null) =>
  response?.outcome.kind === "SUCCEEDED" ? response.outcome.data.requester : null;
/** Failed refreshes never grant access. Only caller-owned inactive status remains for retry feedback. */
const requesterForDisplay = (
  current: RequesterState | null,
  previous: RequesterState | null,
  loading: boolean,
) => {
  if (current !== null) {
    return current;
  }
  if (loading || previous?.kind !== "ACTIVE") {
    return previous;
  }
  return null;
};
const useWorkspaceAccess = (route: Route) => {
  const context = useEndpoint("context", {});
  const requester = requesterForDisplay(
    succeededRequester(context.value),
    succeededRequester(context.previous),
    context.loading,
  );
  const source = requester === succeededRequester(context.value) ? context.value : context.previous;
  const presentation =
    requester === null || source?.outcome.kind !== "SUCCEEDED" ? null : source.outcome.view;
  const actorContext = requester?.kind === "ACTIVE" ? requester : null;
  const views = actorContext?.views ?? [];
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
  capabilities: ReadonlyArray<string>;
  known: boolean;
};
const WorkspaceArea = ({
  views,
  noViews,
  capabilities,
  known,
  ...routing
}: WorkspaceRouteProps) => {
  const active = category(routing.route);
  return (
    <WorkspaceContents
      key={active}
      advertised={
        routing.route.screen === "missing" || availableRoute(routing.route, views, capabilities)
      }
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
/** Feedback is a disclosure owned by this verified requester and session generation. */
const useAcceptanceFeedback = (observedOwner: string | null, caseAvailable: boolean) => {
  const { state: session } = useSessionContext();
  const [accepted, setAccepted] = useState<{ owner: object; receipt: CaseOperationReceipt } | null>(
    null,
  );
  const credential = session.kind === "authenticated" ? session.token : null;
  const feedbackOwner = useMemo(
    () =>
      credential === null || observedOwner === null || !caseAvailable
        ? null
        : { identity: observedOwner, epoch: session.epoch, credential },
    [credential, observedOwner, session.epoch, caseAvailable],
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
    receipt: accepted?.owner === feedbackOwner ? accepted.receipt : null,
    clear: () => {
      setAccepted(null);
    },
    accept: (receipt: CaseOperationReceipt) => {
      if (feedbackOwner === null || latestFeedbackOwner.current !== feedbackOwner) {
        return false;
      }
      setAccepted({ owner: feedbackOwner, receipt });
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

type WorkspaceOwner = {
  actorId: string;
  installationId: string;
  epoch: number;
  credential: string;
};
const useVerifiedWorkspaceOwner = (requester: RequesterState_ACTIVE | null) => {
  const { state: session } = useSessionContext();
  const [owner, setOwner] = useState<WorkspaceOwner | null>(null);
  if (
    requester !== null &&
    session.kind === "authenticated" &&
    (owner === null ||
      owner.actorId !== requester.actorId ||
      owner.installationId !== requester.installationId ||
      owner.epoch !== session.epoch ||
      owner.credential !== session.token)
  ) {
    setOwner({
      actorId: requester.actorId,
      installationId: requester.installationId,
      epoch: session.epoch,
      credential: session.token,
    });
  }
  return { owner, session };
};
/** Retain local consent only for the same verified actor and authenticated session. */
const retainInactiveWorkspace = (
  requester: RequesterState | null,
  owner: WorkspaceOwner | null,
  session: SessionState,
) => {
  if (requester === null || requester.kind === "ACTIVE") {
    return true;
  }
  if (requester.kind === "NOT_REGISTERED" || owner === null || session.kind !== "authenticated") {
    return false;
  }
  return (
    requester.self.actorId === owner.actorId &&
    session.epoch === owner.epoch &&
    session.token === owner.credential &&
    (requester.kind !== "NO_ROLES" || requester.installationId === owner.installationId)
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
  inactive: Exclude<RequesterState, { kind: "ACTIVE" }> | null;
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

export const Dashboard = () => {
  const route = useRoute();
  const { context, requester, actorContext, presentation, views, active, noViews } =
    useWorkspaceAccess(route);
  const cache = useEndpointCache();
  const { owner, session } = useVerifiedWorkspaceOwner(actorContext);
  const ownerIdentity = owner === null ? null : `${owner.installationId}:${owner.actorId}`;
  const observedOwner =
    actorContext === null ? null : `${actorContext.installationId}:${actorContext.actorId}`;

  const feedback = useAcceptanceFeedback(observedOwner, views.includes("CASES"));
  const caseNavigation = useCaseNavigation(feedback.owner);
  const [locked, setLocked] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const content = useRouteHeading(route, ownerIdentity);
  useNavigationLock(locked);
  const businessDate = useBusinessDate(actorContext);
  const shellContext = actorContext ?? { capabilities: [], offers: [] };
  const committed = (receipt: CaseOperationReceipt) => {
    if (!feedback.accept(receipt)) {
      return;
    }
    setNavigationLock(false);
    setLocked(false);
    cache?.invalidate();
    setRefresh((value) => value + 1);
    navigate(casePath(receipt.subject.caseId));
  };
  const inactive = requester !== null && requester.kind !== "ACTIVE" ? requester : null;
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
                capabilities={shellContext.capabilities}
                offers={shellContext.offers}
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
                <MaintenanceNotice active={actorContext?.maintenance.active ?? false} />
                <div ref={content}>
                  <WorkspaceRoute
                    key={ownerIdentity}
                    route={route}
                    caseNavigation={caseNavigation}
                    views={views}
                    noViews={noViews}
                    capabilities={shellContext.capabilities}
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
