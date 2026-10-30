import "./workspace.css";
import { RequesterContext } from "../hooks/useRequesterContext";
import { WorkspaceHeader } from "./WorkspaceHeader";
import { UnresolvedNotice } from "./operation/UnresolvedNotice";
import { RoutedContent } from "./RoutedContent";
import { useEffect, useRef, useState, type ReactNode } from "react";
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
const useRouteHeading = (route: Route) => {
  const identity = route.screen === "cases" ? "cases" : JSON.stringify(route);
  const content = useRef<HTMLDivElement>(null);
  const focused = useRef<string | null>(null);
  useEffect(() => {
    const target = content.current;
    if (target === null || focused.current === identity) {
      return;
    }
    const focusId = takeRouteFocus();
    const focus = () => {
      const heading =
        focusId === null
          ? target.querySelector<HTMLElement>("h1")
          : document.getElementById(focusId);
      if (heading) {
        focused.current = identity;
        if (heading.matches("h1,h2,h3")) {
          heading.tabIndex = -1;
        }
        heading.focus();
        return true;
      }
      return false;
    };
    if (focus()) {
      return;
    }
    const observer = new MutationObserver(() => {
      if (focus()) {
        observer.disconnect();
      }
    });
    observer.observe(target, { childList: true, subtree: true });
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
    route.reference !== receipt.subject.caseReference
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
  useEffect(() => {
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
/** Only the authority editor keeps its operation state; fresh context still fences all disclosures. */
const WorkspaceContents = ({
  advertised,
  retainOperation,
  noViews,
  fallback,
  children,
}: {
  advertised: boolean;
  retainOperation: boolean;
  noViews: boolean;
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
  return noViews ? (
    <h1 tabIndex={-1}>
      <Message id="ui.noViews" />
    </h1>
  ) : null;
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
      retainOperation={routing.route.screen === "authority"}
      noViews={noViews}
      fallback={<WorkspaceUnavailable route={routing.route} />}
    >
      <RoutedContent {...routing} authorityAvailable={views.includes("AUTHORITY")} />
    </WorkspaceContents>
  );
};
export const Dashboard = () => {
  const route = useRoute();
  const [accepted, setAccepted] = useState<CaseReceipt | null>(null);
  const { context, response, views, active } = useWorkspaceAccess(route);
  const cache = useEndpointCache();
  const [locked, setLocked] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const content = useRouteHeading(route);
  useNavigationLock(locked);
  const committed = (receipt: CaseReceipt) => {
    setNavigationLock(false);
    setLocked(false);
    cache?.invalidate();
    setRefresh((value) => value + 1);
    setAccepted(receipt);
    navigate(casePath(receipt.subject.caseReference));
  };
  return (
    <RequesterContext value={response?.outcome.kind === "SUCCEEDED" ? response.outcome.data : null}>
      <WorkspaceHeader active={active} views={views} locked={locked} />
      <UnresolvedNotice />
      <AcceptedToast
        route={route}
        receipt={accepted}
        close={() => {
          setAccepted(null);
        }}
      />
      <main className="app-shell">
        <WorkspaceStatus context={context} />
        <div ref={content}>
          <WorkspaceRoute
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
    </RequesterContext>
  );
};
