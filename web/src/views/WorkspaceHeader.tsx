import {
  type Screen,
  type TaskId,
  type Link,
  type PresentedValue as Value,
} from "@contracts/client";
import { apiEndpoints } from "../api/service";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { coreLinkPath } from "../routing/coreLinks";
import { renderCatalogKey } from "../presentation/messages";
import { useRecovery } from "../recovery/RecoveryContext";
import { PresentedValue } from "../components/PresentedValue";
import { useWorkspaceViews } from "./WorkspaceViews";
import { useCompact } from "../hooks/useCompact";
import { useEffect, useId, useRef } from "react";

import { usePresentation } from "../presentation/context";
import { useSessionContext } from "../session/SessionContext";
import { navigate, guardedDeparture } from "../routing/navigation";
import { followLink } from "../routing/followLink";
import type { Route } from "../routing/routes";
import { Icon } from "../ui/icons";
import { worklistPath } from "../routing/worklistQuery";
import {} from "./shell.css";

const areas = [
  { name: "cases", path: "/cases", label: "label.cases", icon: "cases" },
  { name: "reminders", path: "/reminders", label: "label.reminders", icon: "reminders" },
  { name: "recovery", path: "/recovery", label: "ui.recovery", icon: "history" },
  { name: "people", path: "/people", label: "view.PEOPLE.label", icon: "people" },
  {
    name: "peopleHistory",
    path: "/people-history",
    label: "endpoint.people.history.label",
    icon: "history",
  },
  {
    name: "disclosureTrail",
    path: "/disclosure-trail",
    label: "endpoint.disclosure.trail.label",
    icon: "history",
  },
  { name: "settings", path: "/settings", label: "ui.settings", icon: "settings" },
] as const;
type Area = (typeof areas)[number];
const workspaceLinkId = (area: Area) => `workspace-link-${area.name}`;
export const isWorkspaceLinkId = (id: string) => areas.some((area) => workspaceLinkId(area) === id);
type NavigationCount = { value: Value; timeZone: string };
const useNavigationCounts = () => {
  const views = useWorkspaceViews();
  const recovery = useRecovery();
  const counts = new Map<Area["name"], NavigationCount>();
  const cases = views.cases.loading ? null : views.cases.data?.navigation;
  const reminders = views.reminders.loading ? null : views.reminders.data?.navigation;
  if (cases !== null && cases !== undefined) {
    counts.set("cases", { value: cases, timeZone: views.cases.data!.basis.timeZone });
  }
  if (reminders !== null && reminders !== undefined) {
    counts.set("reminders", { value: reminders, timeZone: views.reminders.data!.basis.timeZone });
  }
  counts.set("recovery", {
    value: { kind: "COUNT", value: recovery.operations.length },
    timeZone: "UTC",
  });
  return counts;
};
const ShellLink = ({
  area,
  active,
  current,
  locked,
  counts,
}: {
  area: Area;
  active: string;
  current: Route["screen"] | null;
  locked: boolean;
  counts: ReadonlyMap<Area["name"], NavigationCount>;
}) => {
  const p = usePresentation();
  const requester = useRequesterContext();
  const coreCaption = requester?.links.find(
    (named) => named.rel === "VIEW" && coreLinkPath(named.link)?.split("?")[0] === area.path,
  )?.labelId;
  const count = counts.get(area.name);
  const captionId = useId();
  return (
    <a
      id={workspaceLinkId(area)}
      href={area.path}
      aria-labelledby={count === undefined ? captionId : `${captionId} ${captionId}-count`}
      data-active-area={active === area.name || undefined}
      aria-current={current === area.name ? "page" : undefined}
      aria-disabled={locked || undefined}
      onClick={(event) => {
        followLink(event, () => {
          navigate(area.path);
        });
      }}
    >
      <Icon name={area.icon} />
      <span id={captionId} className="shell-link-label">
        {coreCaption === undefined ? p.text(area.label) : renderCatalogKey(p, coreCaption)}
      </span>
      {count === undefined ? null : (
        <>
          {" "}
          <span
            id={`${captionId}-count`}
            className="shell-count"
            data-active={(count.value.kind === "COUNT" && count.value.value > 0) || undefined}
          >
            <PresentedValue value={count.value} timeZone={count.timeZone} />
          </span>
        </>
      )}
    </a>
  );
};
const displayEntries = (view: Screen | null, role: "FACTS" | "IDENTITY") =>
  view?.regions.flatMap((region) =>
    region.role === role && region.content.kind === "ENTRIES" ? region.content.entries : [],
  ) ?? [];
const BusinessDate = ({ view }: { view: Screen | null }) => {
  const p = usePresentation();
  const entries = displayEntries(view, "FACTS");
  const date = entries.find((entry) => entry.value.kind === "DATE")?.value;
  const zone = entries.find((entry) => entry.value.kind === "TEXT")?.value;
  return date?.kind !== "DATE" || zone?.kind !== "TEXT" ? null : (
    <section
      className="shell-business-date"
      aria-label={p.text("ui.workDateBasis", { date: p.dateHeading(date.value), zone: zone.value })}
    >
      <Icon name="calendar" />
      <div>
        <time dateTime={date.value}>{p.dateHeading(date.value)}</time>
        <span>{p.text("label.businessDate")}</span>
        <bdi>{zone.value}</bdi>
      </div>
    </section>
  );
};
const AccountMenu = ({ locked, view }: { locked: boolean; view: Screen | null }) => {
  const p = usePresentation();
  const entries = displayEntries(view, "IDENTITY");
  const identity = entries.find((entry) => entry.key === "self")?.value;
  const roles = entries.find((entry) => entry.key === "roles")?.value;
  const { logout } = useSessionContext();
  return (
    <details className="account-menu">
      <summary title={p.text("label.account")}>
        <span className="account-summary-avatar" aria-hidden="true">
          <Icon name="person" />
        </span>
        <span className="account-summary-name">{p.text("label.account")}</span>
        <Icon name="chevron-down" />
      </summary>
      <div className="account-menu-items">
        <div className="account-expanded-identity">
          {identity === undefined ? (
            p.text("label.account")
          ) : (
            <PresentedValue value={identity} timeZone="UTC" avatar />
          )}
          {roles === undefined || (roles.kind === "CODES" && roles.tokens.length === 0) ? null : (
            <div className="account-roles" aria-label={p.text("field.grants.label")}>
              <PresentedValue value={roles} timeZone="UTC" />
            </div>
          )}
        </div>
        <a
          href="/settings"
          aria-disabled={locked || undefined}
          onClick={(event) => {
            followLink(event, () => {
              if (navigate("/settings")) {
                event.currentTarget.closest("details")?.removeAttribute("open");
              }
            });
          }}
        >
          {p.text("ui.settings")}
        </a>
        <button
          type="button"
          disabled={locked}
          onClick={() => {
            guardedDeparture(() => {
              void logout();
            });
          }}
        >
          {p.text("ui.signOut")}
        </button>
      </div>
    </details>
  );
};
const SearchCases = ({ locked }: { locked: boolean }) => {
  const p = usePresentation();
  const path = worklistPath({ view: "ALL" });
  return (
    <a
      className="shell-search"
      href={path}
      aria-disabled={locked || undefined}
      onClick={(event) => {
        followLink(event, () => {
          navigate(path, false, "worklist-search");
        });
      }}
    >
      <Icon name="search" />
      {p.text("ui.shell.searchCases")}
    </a>
  );
};

const MoreNavigation = ({
  items,
  active,
  current,
  locked,
  counts,
}: {
  items: ReadonlyArray<Area>;
  active: string;
  current: Route["screen"] | null;
  locked: boolean;
  counts: ReadonlyMap<Area["name"], NavigationCount>;
}) => {
  const p = usePresentation();
  const menu = useRef<HTMLDetailsElement>(null);
  const trigger = useRef<HTMLElement>(null);
  useEffect(() => {
    const disclosure = menu.current;
    if (disclosure === null) {
      return;
    }
    const escape = (event: KeyboardEvent) => {
      if (event.key === "Escape" && disclosure.open) {
        event.preventDefault();
        disclosure.open = false;
        trigger.current?.focus();
      }
    };
    disclosure.addEventListener("keydown", escape);
    return () => {
      disclosure.removeEventListener("keydown", escape);
    };
  }, []);
  return (
    <details className="compact-navigation" ref={menu}>
      <summary ref={trigger}>{p.text("ui.shell.administration")}</summary>
      <div className="compact-navigation-links">
        {items.map((area) => (
          <ShellLink
            key={area.name}
            area={area}
            active={active}
            current={current}
            locked={locked}
            counts={counts}
          />
        ))}
      </div>
    </details>
  );
};

export const WorkspaceHeader = ({
  route,
  presentation,
  active,
  views,
  tasks,
  locked,
}: {
  route: Route;
  presentation: Screen | null;
  active: string;
  views: ReadonlyArray<Link["endpoint"]>;
  tasks: ReadonlyArray<TaskId>;
  locked: boolean;
}) => {
  const p = usePresentation();
  const compact = useCompact();
  const view = presentation;
  const counts = useNavigationCounts();
  const cases = views.includes(apiEndpoints.caseList);
  const available: Record<Area["name"], boolean> = {
    cases,
    reminders: cases,
    people: views.includes(apiEndpoints.peopleList),
    peopleHistory: tasks.includes(apiEndpoints.peopleHistory),
    disclosureTrail: views.includes(apiEndpoints.disclosureTrail),
    recovery: true,
    settings: true,
  };
  const offered = areas.filter(({ name }) => available[name]);
  const primary = offered.filter(({ name }) => name === "cases" || name === "reminders");
  const additional = offered.filter((area) => !primary.includes(area));
  const desktopAdministration = additional.filter(({ name }) => name !== "settings");
  const current =
    ("actorId" in route && route.actorId !== "") ||
    (route.screen === "recovery" && route.operationId !== "")
      ? null
      : route.screen;
  const title =
    areas.find((area) => area.name === active)?.label ??
    (route.screen === "recovery" ? "ui.recovery" : "label.caseDetail");
  return (
    <>
      <aside className="workspace-sidebar">
        {compact ? (
          <BusinessDate view={view} />
        ) : (
          <>
            <a
              className="product-name"
              href="/"
              onClick={(event) => {
                followLink(event, () => {
                  navigate("/");
                });
              }}
              aria-disabled={locked || undefined}
            >
              <span className="product-mark" aria-hidden="true">
                C
              </span>
              <span className="shell-brand">ClaimCore</span>
            </a>
            <nav aria-label={p.text("ui.primary")}>
              {primary.map((area) => (
                <ShellLink
                  key={area.name}
                  area={area}
                  active={active}
                  current={current}
                  locked={locked}
                  counts={counts}
                />
              ))}
              <div className="shell-admin">
                <p>{p.text("ui.shell.administration")}</p>
                {desktopAdministration.map((area) => (
                  <ShellLink
                    key={area.name}
                    area={area}
                    active={active}
                    current={current}
                    locked={locked}
                    counts={counts}
                  />
                ))}
              </div>
            </nav>
            <footer>
              <BusinessDate view={view} />
              <AccountMenu locked={locked} view={view} />
            </footer>
          </>
        )}
      </aside>
      <header className="workspace-header">
        <span className="shell-screen-title">{p.text(title)}</span>
        {cases ? <SearchCases locked={locked} /> : null}
      </header>
      {compact ? (
        <nav className="workspace-mobile-tabs" aria-label={p.text("ui.primary")}>
          {primary.map((area) => (
            <ShellLink
              key={area.name}
              area={area}
              active={active}
              current={current}
              locked={locked}
              counts={counts}
            />
          ))}
          <MoreNavigation
            items={additional}
            key={JSON.stringify(route)}
            active={active}
            current={current}
            locked={locked}
            counts={counts}
          />
        </nav>
      ) : null}
    </>
  );
};
