import { useWorkspaceViews } from "./WorkspaceViews";
import { useCompact } from "../hooks/useCompact";
import { useEffect, useRef, useState } from "react";
import type { InstallationOffer } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { useSessionContext } from "../session/SessionContext";
import { navigate, guardedDeparture } from "../routing/navigation";
import { followLink } from "../routing/followLink";
import type { Route } from "../routing/routes";
import { Icon } from "../ui/icons";
import "./shell.css";

const areas = [
  { name: "cases", path: "/cases", label: "ui.cases", icon: "cases" },
  { name: "reminders", path: "/reminders", label: "ui.reminders", icon: "reminders" },
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
const BusinessDate = () => {
  const p = usePresentation();
  const requester = useRequesterContext();
  return requester === null ? null : (
    <section
      className="shell-business-date"
      aria-label={p.text("ui.workDateBasis", {
        date: p.dateHeading(requester.businessDate),
        zone: requester.timeZone,
      })}
      title={p.text("ui.workDateBasis", {
        date: p.dateHeading(requester.businessDate),
        zone: requester.timeZone,
      })}
    >
      <Icon name="calendar" />
      <div>
        <time dateTime={requester.businessDate}>{p.dateHeading(requester.businessDate)}</time>
        <span>{p.text("ui.shell.businessDate")}</span>
        <bdi>{requester.timeZone}</bdi>
      </div>
    </section>
  );
};
const AccountMenu = ({ locked }: { locked: boolean }) => {
  const p = usePresentation();
  const { logout } = useSessionContext();
  return (
    <details className="account-menu">
      <summary title={p.text("ui.account")}>
        <Icon name="person" />
        <span>{p.text("ui.account")}</span>
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
      <button type="submit" disabled={locked}>
        {p.text("ui.search")}
      </button>
    </form>
  );
};
export const WorkspaceHeader = ({
  route,
  active,
  views,
  capabilities,
  offers,
  locked,
  openCase,
}: {
  route: Route;
  active: string;
  views: ReadonlyArray<string>;
  capabilities: ReadonlyArray<string>;
  offers: ReadonlyArray<InstallationOffer>;
  locked: boolean;
  openCase: () => void;
}) => {
  const p = usePresentation();
  const compact = useCompact();
  const counts = useNavigationCounts();
  const cases = views.includes("CASES");
  const primary = areas.filter((area) => ["cases", "reminders"].includes(area.name) && cases);
  const admin = areas.filter(
    (area) =>
      (area.name === "people" && views.includes("AUTHORITY")) ||
      (area.name === "authorityHistory" && capabilities.includes("READ_AUTHORITY")),
  );
  const mobile = [...primary, ...admin.filter((area) => area.name === "people"), areas[4]];
  const title =
    areas.find((area) => area.name === active)?.label ??
    (route.screen === "recovery" ? "ui.recovery" : "ui.caseDetail");
  return (
    <>
      <aside className="workspace-sidebar">
        {compact ? (
          <BusinessDate />
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
              <BusinessDate />
              <AccountMenu locked={locked} />
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
