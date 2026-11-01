import { MaintenanceNotice } from "../components/MaintenanceNotice";
import { RecoveryProvider } from "../recovery/RecoveryContext";
import "./workspace.css";
import { useSessionContext } from "../session/SessionContext";
import { RequesterContext } from "../hooks/useRequesterContext";
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
const category = (route: Route): "cases" | "operations" | "authority" =>
  route.screen === "operations" || route.screen === "authority" ? route.screen : "cases";

const useInitialAllowedRoute = (route: Route, views: ReadonlyArray<string>) => {
  const selected = useRef(false);
  useEffect(() => {
    if (selected.current || views.length === 0) {
      return;
    }
    selected.current = true;
    if (!views.includes(category(route).toUpperCase())) {
      const first = (["cases", "authority", "operations"] as const).find((name) =>
        views.includes(name.toUpperCase()),
      );
      if (first !== undefined) {
        navigate(`/${first}`, true);
      }
    }
  }, [route, views]);
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
    const observer = new MutationObserver(() => {
      if (focus()) {
        observer.disconnect();
      }
    });
    observer.observe(target, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ["aria-busy"],
    });
    return () => {
      observer.disconnect();
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
  const views = response?.outcome.kind === "SUCCEEDED" ? response.outcome.data.views : [];
  const active = category(route);
  useInitialAllowedRoute(route, views);
  return { context, response, views, active };
};
const WorkspaceUnavailable = ({ route }: { route: Route }) => {
  const p = usePresentation();
  let title = p.text("ui.cases");
  if (route.screen === "operations") {
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
};
const WorkspaceRoute = ({ views, noViews, ...routing }: WorkspaceRouteProps) => {
  const active = category(routing.route);
  return (
    <WorkspaceContents
      key={active}
      advertised={views.includes(active.toUpperCase())}
      retainOperation={["authority", "caseAct", "newCase"].includes(routing.route.screen)}
      noViews={noViews}
      known={views.length > 0}
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
export const Dashboard = () => {
  const route = useRoute();
  const { context, response, views, active } = useWorkspaceAccess(route);
  const cache = useEndpointCache();
  const [owner, setOwner] = useState<string | null>(null);
  const observedOwner =
    response?.outcome.kind === "SUCCEEDED"
      ? `${response.outcome.data.installationId}:${response.outcome.data.actorId}`
      : null;
  if (observedOwner !== null && observedOwner !== owner) {
    setOwner(observedOwner);
  }
  const feedback = useAcceptanceFeedback(observedOwner, views.includes("CASES"));
  const [locked, setLocked] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const content = useRouteHeading(route, owner);
  useNavigationLock(locked);
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
    <RequesterContext value={response?.outcome.kind === "SUCCEEDED" ? response.outcome.data : null}>
      <RecoveryProvider>
        <WorkspaceHeader active={active} views={views} locked={locked} />
        <AcceptedToast
          route={route}
          receipt={feedback.receipt}
          close={() => {
            feedback.clear();
          }}
        />
        <main className="app-shell">
          <WorkspaceStatus context={context} />
          <MaintenanceNotice
            active={
              response?.outcome.kind === "SUCCEEDED" && response.outcome.data.maintenance.active
            }
          />
          <div ref={content}>
            <WorkspaceRoute
              key={owner}
              route={route}
              views={views}
              noViews={
                !context.loading && response?.outcome.kind === "SUCCEEDED" && views.length === 0
              }
              setLocked={setLocked}
              committed={committed}
              refresh={refresh}
            />
          </div>
        </main>
      </RecoveryProvider>
    </RequesterContext>
  );
};
