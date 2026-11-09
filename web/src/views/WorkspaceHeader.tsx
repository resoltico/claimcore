import { useRecovery } from "../recovery/RecoveryContext";
import { PresentedValue } from "../components/PresentedValue";
import { ActorIdentity } from "../components/ActorIdentity";
import { useWorkspaceViews } from "./WorkspaceViews";
import { useCompact } from "../hooks/useCompact";
import { useEffect, useRef, useState } from "react";
import type { Screen, InstallationOffer } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import { useSessionContext } from "../session/SessionContext";
import { navigate, guardedDeparture } from "../routing/navigation";
import { followLink } from "../routing/followLink";
import type { Route } from "../routing/routes";
import { Icon } from "../ui/icons";
import { Button } from "../ui/Button";
import "./shell.css";

const areas = [
  { name: "cases", path: "/cases", label: "ui.cases", icon: "cases" },
  { name: "reminders", path: "/reminders", label: "ui.reminders", icon: "reminders" },
  { name: "recovery", path: "/recovery", label: "ui.recovery", icon: "history" },
  { name: "people", path: "/people", label: "ui.shell.people", icon: "people" },
  {
    name: "authorityHistory",
    path: "/authority-history",
    label: "ui.shell.authorityHistory",
    icon: "history",
  },
  { name: "settings", path: "/settings", label: "ui.settings", icon: "settings" },
] as const;
type Area = (typeof areas)[number];
const useNavigationCounts = () => {
  const views = useWorkspaceViews();
  const recovery = useRecovery();
  const counts = new Map<Area["name"], number>();
  const attention = views.cases.loading
    ? undefined
    : views.cases.data?.items.find((item) => item.view === "NEEDS_ATTENTION");
  if (attention !== undefined) {
    counts.set("cases", attention.count);
  }
  const reminders = views.reminders.loading ? undefined : views.reminders.data?.items;
  const overdue = reminders?.find((item) => item.view === "OVERDUE");
  const today = reminders?.find((item) => item.view === "TODAY");
  // The core's pending OVERDUE and TODAY views are disjoint within this one snapshot (W3).
  if (overdue !== undefined && today !== undefined) {
    counts.set("reminders", overdue.count + today.count);
  }
  counts.set("recovery", recovery.operations.length);
  return counts;
};
const ShellLink = ({
  area,
  active,
  locked,
  counts,
}: {
  area: Area;
  active: string;
  locked: boolean;
  counts: ReadonlyMap<Area["name"], number>;
}) => {
  const p = usePresentation();
  const count = counts.get(area.name);
  const label =
    count === undefined ? p.text(area.label) : `${p.text(area.label)} ${p.integer(count)}`;
  return (
    <a
      href={area.path}
      aria-label={label}
      aria-current={active === area.name ? "page" : undefined}
      aria-disabled={locked || undefined}
      onClick={(event) => {
        followLink(event, () => {
          navigate(area.path);
        });
      }}
    >
      <Icon name={area.icon} />
      <span className="shell-link-label" aria-hidden="true">
        {p.text(area.label)}
      </span>
      {count === undefined ? null : (
        <>
          {" "}
          <span className="shell-count" data-active={count > 0 || undefined} aria-hidden="true">
            {p.integer(count)}
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
        <span>{p.text("ui.shell.businessDate")}</span>
        <bdi>{zone.value}</bdi>
      </div>
    </section>
  );
};
const AccountMenu = ({ locked, view }: { locked: boolean; view: Screen | null }) => {
  const p = usePresentation();
  const entries = displayEntries(view, "IDENTITY");
  const actor = entries.find((entry) => entry.value.kind === "ACTOR")?.value;
  const roles = entries.find(
    (entry) => entry.value.kind === "CODES" && entry.value.vocabulary === "role",
  )?.value;
  const { logout } = useSessionContext();
  return (
    <details className="account-menu">
      <summary title={p.text("ui.account")}>
        <div className="account-identity">
          {actor?.kind === "ACTOR" ? (
            <ActorIdentity actor={actor.value} avatar />
          ) : (
            p.text("ui.account")
          )}
          {roles?.kind !== "CODES" || roles.tokens.length === 0 ? null : (
            <div className="account-roles" aria-label={p.text("ui.people.roles")}>
              <PresentedValue value={roles} timeZone="UTC" />
            </div>
          )}
        </div>
        <Icon name="chevron-down" />
      </summary>
      <div className="account-menu-items">
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
const useSearchShortcut = (search: React.RefObject<HTMLInputElement | null>) => {
  useEffect(() => {
    const key = (event: KeyboardEvent) => {
      const { target } = event;
      if (
        event.key === "/" &&
        !event.ctrlKey &&
        !event.metaKey &&
        !event.altKey &&
        !(
          target instanceof HTMLElement &&
          (target.isContentEditable ||
            target.closest("input,textarea,[role=combobox],[role=listbox],[role=dialog]") !== null)
        )
      ) {
        const input = search.current;
        if (input !== null && input.getClientRects().length > 0 && !input.disabled) {
          event.preventDefault();
          input.focus();
        }
      }
    };
    window.addEventListener("keydown", key);
    return () => {
      window.removeEventListener("keydown", key);
    };
  }, [search]);
};
const ShellSearch = ({ locked }: { locked: boolean }) => {
  const p = usePresentation();
  const [query, setQuery] = useState("");
  const search = useRef<HTMLInputElement>(null);
  useSearchShortcut(search);
  return (
    <form
      className="shell-search"
      role="search"
      aria-label={p.text("ui.shell.searchCases")}
      onSubmit={(event) => {
        event.preventDefault();
        navigate(`/cases?q=${encodeURIComponent(query)}`);
      }}
    >
      <label htmlFor="shell-case-search" className="sr-only">
        {p.text("ui.shell.searchCases")}
      </label>
      <Icon name="search" />
      <input
        ref={search}
        id="shell-case-search"
        type="search"
        value={query}
        disabled={locked}
        onChange={(event) => {
          setQuery(event.target.value);
        }}
        placeholder={p.text("ui.shell.searchCases")}
      />
      <Button type="submit" variant="secondary" isDisabled={locked}>
        {p.text("ui.search")}
      </Button>
    </form>
  );
};
export const WorkspaceHeader = ({
  route,
  presentation,
  active,
  views,
  capabilities,
  offers,
  locked,
  openCase,
}: {
  route: Route;
  presentation: Screen | null;
  active: string;
  views: ReadonlyArray<string>;
  capabilities: ReadonlyArray<string>;
  offers: ReadonlyArray<InstallationOffer>;
  locked: boolean;
  openCase: () => void;
}) => {
  const p = usePresentation();
  const compact = useCompact();
  const view = presentation;
  const counts = useNavigationCounts();
  const cases = views.includes("CASES");
  const primary = areas.filter((area) => ["cases", "reminders"].includes(area.name) && cases);
  const admin = areas.filter(
    (area) =>
      (area.name === "people" && views.includes("AUTHORITY")) ||
      (area.name === "authorityHistory" && capabilities.includes("READ_AUTHORITY")),
  );
  const mobile = [
    ...primary,
    ...admin.filter((area) => area.name === "people"),
    areas.find((area) => area.name === "settings")!,
  ];
  const title =
    areas.find((area) => area.name === active)?.label ??
    (route.screen === "recovery" ? "ui.recovery" : "ui.caseDetail");
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
                  locked={locked}
                  counts={counts}
                />
              ))}
              <ShellLink
                area={areas.find((area) => area.name === "recovery")!}
                active={active}
                locked={locked}
                counts={counts}
              />
              {admin.length === 0 ? null : (
                <div className="shell-admin">
                  <p>{p.text("ui.shell.administration")}</p>
                  {admin.map((area) => (
                    <ShellLink
                      key={area.name}
                      area={area}
                      active={active}
                      locked={locked}
                      counts={counts}
                    />
                  ))}
                </div>
              )}
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
        {cases ? <ShellSearch locked={locked} /> : null}
        {route.screen === "cases" && offers.some((offer) => offer.kind === "OPEN") ? (
          <a
            id="open-case"
            className="shell-open-case"
            href="/cases/new"
            aria-disabled={locked || undefined}
            onClick={(event) => {
              followLink(event, () => {
                openCase();
              });
            }}
          >
            {p.actLabel("OPEN")}
          </a>
        ) : null}
      </header>
      {compact ? (
        <nav className="workspace-mobile-tabs" aria-label={p.text("ui.primary")}>
          {mobile.map((area) => (
            <ShellLink
              key={area.name}
              area={area}
              active={active}
              locked={locked}
              counts={counts}
            />
          ))}
        </nav>
      ) : null}
    </>
  );
};
