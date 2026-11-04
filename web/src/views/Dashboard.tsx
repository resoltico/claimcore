import { ReadRetryStatus } from "./ReadRetryStatus";
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
import type { CaseReceipt } from "../api/service";
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
  return { element: requested ?? heading, restored: requested !== null };
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
  receipt: CaseReceipt | null;
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
    <div className="success-toast" role="status">
      {p.text("ui.caseRecorded", {
        act: p.actLabel(receipt.subject.act),
        revision: p.integer(receipt.subject.revision),
      })}
      {receipt.subject.act === "OPEN" ? <p>{p.text("ui.planAfterOpen")}</p> : null}
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
const useWorkspaceAccess = (route: Route) => {
  const context = useEndpoint("context", {});
  const response = context.value ?? (context.loading ? context.previous : null);
  const actorContext = response?.outcome.kind === "SUCCEEDED" ? response.outcome.data : null;
  const views = actorContext?.views ?? [];
  const active = category(route);
  const noViews = !context.loading && actorContext !== null && views.length === 0;
  useInitialAllowedRoute(route, views, actorContext !== null);
  return { context, actorContext, views, active, noViews };
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
const WorkspaceRoute = ({
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
/** Feedback is a disclosure owned by this verified requester and session generation. */
const useAcceptanceFeedback = (observedOwner: string | null, caseAvailable: boolean) => {
  const { state: session } = useSessionContext();
  const [accepted, setAccepted] = useState<{ owner: object; receipt: CaseReceipt } | null>(null);
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
    receipt: accepted?.owner === feedbackOwner ? accepted.receipt : null,
    clear: () => {
      setAccepted(null);
    },
    accept: (receipt: CaseReceipt) => {
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

export const Dashboard = () => {
  const route = useRoute();
  const { context, actorContext, views, active, noViews } = useWorkspaceAccess(route);
  const cache = useEndpointCache();
  const [owner, setOwner] = useState<string | null>(null);
  const observedOwner =
    actorContext === null ? null : `${actorContext.installationId}:${actorContext.actorId}`;
  if (observedOwner !== null && observedOwner !== owner) {
    setOwner(observedOwner);
  }
  const feedback = useAcceptanceFeedback(observedOwner, views.includes("CASES"));
  const [locked, setLocked] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const content = useRouteHeading(route, owner);
  useNavigationLock(locked);
  const businessDate = useBusinessDate(actorContext);
  const shellContext = actorContext ?? { capabilities: [], offers: [] };
  const committed = (receipt: CaseReceipt) => {
    if (!feedback.accept(receipt)) {
      return;
    }
    setNavigationLock(false);
    setLocked(false);
    cache?.invalidate();
    setRefresh((value) => value + 1);
    navigate(casePath(receipt.subject.caseId));
  };
  return (
    <RequesterContext value={actorContext}>
      <RecoveryProvider>
        <div className="workspace-shell">
          <WorkspaceHeader
            route={route}
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
            <ReadRetryStatus />
            {businessDate.changedDate === null ? null : (
              <BusinessDateNotice date={businessDate.changedDate} refresh={businessDate.refresh} />
            )}
            <WorkspaceStatus context={context} />
            <MaintenanceNotice active={actorContext?.maintenance.active ?? false} />
            <div ref={content}>
              <WorkspaceRoute
                key={owner}
                route={route}
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
      </RecoveryProvider>
    </RequesterContext>
  );
};
