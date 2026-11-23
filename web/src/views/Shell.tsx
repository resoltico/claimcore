import { actorName } from "../presentation/actorName";
import {
  type Screen,
  type TaskId,
  type Link,
  type PresentedValue as Value,
} from "@contracts/client";
import { apiEndpoints } from "../api/service";
import { useCaseCreationControl, useRequesterContext } from "../hooks/useRequesterContext";
import { coreLinkPath } from "../routing/coreLinks";
import { renderCatalogKey } from "../presentation/messages";
import { useRecovery } from "../recovery/RecoveryContext";
import { PresentedValue } from "../components/PresentedValue";
import { useWorkspaceViews } from "./WorkspaceViews";
import { useCompact } from "../hooks/useCompact";
import { useEffect, useId, useState, type ComponentProps } from "react";

import { usePresentation } from "../presentation/context";
import { useSessionContext } from "../session/SessionContext";
import { navigate, guardedDeparture } from "../routing/navigation";
import { followLink } from "../routing/followLink";
import type { Route } from "../routing/routes";
import { Icon } from "../ui/icons";
import { ActionMenu } from "../ui/ActionMenu";
import { Button } from "../ui/Button";
import { Dialog } from "../ui/Dialog";
import { FindCase } from "./FindCase";
import {} from "./shell.css";

const areas = [
  { name: "cases", path: "/cases", label: "label.cases", icon: "cases" },
  { name: "reminders", path: "/reminders", label: "label.reminders", icon: "reminders" },
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
    icon: "audit",
  },
  { name: "settings", path: "/settings", label: "ui.settings", icon: "settings" },
] as const;
type Area = (typeof areas)[number];
const workspaceLinkId = (area: Area) => `workspace-link-${area.name}`;
export const isWorkspaceLinkId = (id: string) => areas.some((area) => workspaceLinkId(area) === id);
type NavigationCount = { value: Value; timeZone: string };
const useNavigationCounts = () => {
  const views = useWorkspaceViews();
  const counts = new Map<Area["name"], NavigationCount>();
  const cases = views.cases.loading ? null : views.cases.data?.navigation;
  const reminders = views.reminders.loading ? null : views.reminders.data?.navigation;
  if (cases !== null && cases !== undefined) {
    counts.set("cases", { value: cases, timeZone: views.cases.data!.basis.timeZone });
  }
  if (reminders !== null && reminders !== undefined) {
    counts.set("reminders", { value: reminders, timeZone: views.reminders.data!.basis.timeZone });
  }
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
      title={coreCaption === undefined ? p.text(area.label) : renderCatalogKey(p, coreCaption)}
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
          <span id={`${captionId}-count`} className="shell-count">
            {area.name === "reminders" && count.value.kind === "COUNT" ? (
              p.text("label.remindersDue", { count: p.integer(count.value.value) })
            ) : (
              <PresentedValue value={count.value} timeZone={count.timeZone} />
            )}
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
    <p className="shell-business-date">
      {p.text("label.businessDateCaption", { date: p.dateHeading(date.value), zone: zone.value })}
    </p>
  );
};

const aboutPath = "/settings?section=about";

const currentArea = (route: Route): Route["screen"] | null =>
  "actorId" in route && route.actorId !== "" ? null : route.screen;

const shellTitle = (active: string, route: Route) =>
  areas.find((area) => area.name === active)?.label ??
  (route.screen === "recovery" ? "ui.recovery" : "label.caseDetail");

const accountCaption = (p: ReturnType<typeof usePresentation>, identity: Value | undefined) => {
  switch (identity?.kind) {
    case "ACTOR":
      return actorName(p, identity.value);
    case "TEXT":
      return identity.value;
    default:
      return null;
  }
};

const ShellAccount = ({
  identity,
  items,
}: {
  identity: Value | undefined;
  items: ComponentProps<typeof ActionMenu>["items"];
}) => {
  const p = usePresentation();
  const label = p.text("label.account");
  const name = accountCaption(p, identity);
  return (
    <ActionMenu
      id="workspace-account"
      label={name === null ? label : `${label} · ${name}`}
      items={items}
      trigger={
        <span className="shell-account-identity">
          <Icon name="person" />
          {name === null ? null : <bdi>{name}</bdi>}
        </span>
      }
    />
  );
};

export const Shell = ({
  route,
  presentation,
  active,
  views,
  tasks,
  locked,
  phoneActionRef,
}: {
  route: Route;
  presentation: Screen | null;
  active: string;
  views: ReadonlyArray<Link["endpoint"]>;
  tasks: ReadonlyArray<TaskId>;
  locked: boolean;
  phoneActionRef?: (node: HTMLDivElement | null) => void;
}) => {
  const p = usePresentation();
  const compact = useCompact();
  const counts = useNavigationCounts();
  const recovery = useRecovery();
  const creation = useCaseCreationControl();
  const { logout } = useSessionContext();
  const [findOrigin, setFindOrigin] = useState<Route | null>(null);
  const [moreOrigin, setMoreOrigin] = useState<Route | null>(null);
  const cases = views.includes(apiEndpoints.caseList);
  const available: Record<Area["name"], boolean> = {
    cases,
    reminders: cases,
    people: views.includes(apiEndpoints.peopleList),
    peopleHistory: tasks.includes(apiEndpoints.peopleHistory),
    disclosureTrail: views.includes(apiEndpoints.disclosureTrail),
    settings: true,
  };
  const offered = areas.filter(({ name }) => available[name]);
  const primary = offered.filter(({ name }) => name === "cases" || name === "reminders");
  const administrative = offered.filter(
    ({ name }) => name === "people" || name === "peopleHistory" || name === "disclosureTrail",
  );
  const current = currentArea(route);
  const title = shellTitle(active, route);
  const identity = displayEntries(presentation, "IDENTITY").find(
    (entry) => entry.key === "self",
  )?.value;
  const apple = /Mac|iPhone|iPad|iPod/u.test(window.navigator.platform);
  const shortcut = apple ? "⌘K" : "Ctrl+K";
  const signOut = () => {
    guardedDeparture(() => {
      void logout();
    });
  };
  const navigationItems = [
    { id: "settings", label: p.text("ui.settings"), path: "/settings" },
    {
      id: "recovery",
      label: p.text("label.recoveryCount", { count: p.integer(recovery.operations.length) }),
      path: "/recovery",
    },
    { id: "about", label: p.text("ui.about"), path: aboutPath },
  ];
  const accountItems = [
    ...navigationItems.map(({ id, label, path }) => ({
      id,
      label,
      disabled: locked,
      select: () => {
        navigate(path);
      },
    })),
    { id: "sign-out", label: p.text("ui.signOut"), disabled: locked, select: signOut },
  ];

  useEffect(() => {
    const keyDown = (event: KeyboardEvent) => {
      if (
        cases &&
        !locked &&
        (apple ? event.metaKey : event.ctrlKey) &&
        !event.altKey &&
        event.key.toLowerCase() === "k"
      ) {
        event.preventDefault();
        setFindOrigin(route);
      }
    };
    window.addEventListener("keydown", keyDown);
    return () => {
      window.removeEventListener("keydown", keyDown);
    };
  }, [cases, locked, apple, route]);

  const link = (area: Area) => (
    <ShellLink
      key={area.name}
      area={area}
      active={active}
      current={current}
      locked={locked}
      counts={counts}
    />
  );
  const find = (iconOnly: boolean) => (
    <Button
      id={iconOnly ? "workspace-phone-find" : "workspace-find"}
      variant="quiet"
      isDisabled={locked}
      aria-label={p.text("ui.findCase")}
      aria-keyshortcuts={apple ? "Meta+K" : "Control+K"}
      className="shell-find"
      onPress={() => {
        setFindOrigin(route);
      }}
    >
      <Icon name="search" />
      {iconOnly ? null : (
        <>
          <span className="shell-link-label">{p.text("ui.findCase")}</span>
          <kbd aria-hidden="true">{shortcut}</kbd>
        </>
      )}
    </Button>
  );

  return (
    <>
      <aside className="workspace-sidebar">
        <a
          className="product-name"
          href="/"
          aria-label="ClaimCore"
          aria-disabled={locked || undefined}
          onClick={(event) => {
            followLink(event, () => {
              navigate("/");
            });
          }}
        >
          <span className="product-mark" aria-hidden="true">
            <Icon name="brand" />
          </span>
          <span className="shell-brand">ClaimCore</span>
        </a>
        {cases ? find(false) : null}
        <nav aria-label={p.text("ui.primary")}>
          {primary.map(link)}
          {administrative.length === 0 ? null : (
            <div className="shell-admin">
              <p>{p.text("label.administration")}</p>
              {administrative.map(link)}
            </div>
          )}
        </nav>
        <footer className="shell-footer">
          <BusinessDate view={presentation} />
          <a
            href={aboutPath}
            title={p.text("ui.about")}
            aria-disabled={locked || undefined}
            onClick={(event) => {
              followLink(event, () => {
                navigate(aboutPath);
              });
            }}
          >
            <Icon name="info" /> <span className="shell-link-label">{p.text("ui.about")}</span>
          </a>
          <ShellAccount identity={identity} items={accountItems} />
        </footer>
      </aside>
      {compact ? (
        <>
          <header className="phone-app-bar">
            <span>{p.text(title)}</span>
            {cases ? find(true) : null}
            {route.screen === "cases" && creation !== undefined ? (
              <Button
                isDisabled={locked}
                compact
                aria-label={renderCatalogKey(p, creation.labelId)}
                onPress={() => {
                  navigate("/cases/new");
                }}
              >
                <Icon name="add" />
              </Button>
            ) : null}
            <div className="phone-primary-action" ref={phoneActionRef} />
          </header>
          <nav className="workspace-mobile-tabs" aria-label={p.text("ui.primary")}>
            {primary.map(link)}
            <Button
              id="workspace-more"
              variant="quiet"
              onPress={() => {
                setMoreOrigin(route);
              }}
            >
              {p.text("ui.more")}
            </Button>
          </nav>
          <Dialog
            title={p.text("ui.more")}
            closeLabel={p.text("ui.close")}
            isOpen={moreOrigin === route}
            isDismissable
            onOpenChange={(open) => {
              setMoreOrigin(open ? route : null);
            }}
            placement="bottom"
          >
            <div className="shell-more">
              {administrative.map(link)}
              {navigationItems.map((item) => (
                <a
                  key={item.id}
                  href={item.path}
                  aria-disabled={locked || undefined}
                  onClick={(event) => {
                    followLink(event, () => {
                      navigate(item.path, false, undefined, undefined, "workspace-more");
                    });
                  }}
                >
                  {item.label}
                </a>
              ))}
              <Button variant="quiet" isDisabled={locked} onPress={signOut}>
                {p.text("ui.signOut")}
              </Button>
              <BusinessDate view={presentation} />
            </div>
          </Dialog>
        </>
      ) : null}
      <FindCase
        open={findOrigin === route}
        returnFocusId={compact ? "workspace-phone-find" : "workspace-find"}
        setOpen={(open) => {
          setFindOrigin(open ? route : null);
        }}
      />
    </>
  );
};
