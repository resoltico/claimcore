import type {
  taskChoices,
  Link,
  HumanReadDocument_work_views,
  PresentedValue,
  WorkDateBasis,
} from "@contracts/client";
import { useEndpointCache } from "../hooks/EndpointProvider";
import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import { createContext, useContext, useMemo, type ReactNode } from "react";

import type { Notice } from "../protocol/notices";
import { useEndpoint } from "../hooks/useEndpoint";
import { useRequesterContext } from "../hooks/useRequesterContext";
type Views<T> = { data: T | null; loading: boolean; message: Notice | null };
type ViewCounts<V> = {
  basis: WorkDateBasis;
  navigation: PresentedValue | null;
  items: ReadonlyArray<{ view: V; label: PresentedValue; count: PresentedValue }>;
};
export type CaseCounts = ViewCounts<(typeof taskChoices.caseView)[number]>;
export type ReminderCounts = ViewCounts<(typeof taskChoices.reminderView)[number]>;
const countData = <V,>(
  document: HumanReadDocument_work_views | null,
  area: "cases" | "reminders",
  selection: (link: Link) => V | undefined,
): ViewCounts<V> | null => {
  if (document === null || document.basis.kind !== "SNAPSHOT") {
    return null;
  }
  const items = document.screen.regions.flatMap((region) => {
    const { content } = region;
    if (content.kind !== "TABLE") {
      return [];
    }
    const labelIndex = content.columns.findIndex((column) => column.key === "view");
    const countIndex = content.columns.findIndex((column) => column.key === "count");
    return content.rows.flatMap((row) => {
      const view = row.link === undefined ? undefined : selection(row.link);
      const label = row.values[labelIndex];
      const count = row.values[countIndex];
      return view === undefined || label === undefined || count === undefined
        ? []
        : [{ view, label, count }];
    });
  });
  const totals = document.screen.regions.flatMap((region) =>
    region.role === "TOTALS" && region.content.kind === "ENTRIES" ? region.content.entries : [],
  );
  return {
    basis: document.basis.observation,
    items,
    navigation: totals.find((entry) => entry.key === area)?.value ?? null,
  };
};
const caseView = (link: Link) => (link.endpoint === "case.list" ? link.input.view : undefined);
const reminderView = (link: Link) =>
  link.endpoint === "reminder.list" ? link.input.view : undefined;
export type WorkspaceViews = { cases: Views<CaseCounts>; reminders: Views<ReminderCounts> };
export const WorkspaceViewsContext = createContext<WorkspaceViews | null>(null);
/** One authenticated shell owns these snapshot counts; renderers never define their predicates. */
export const WorkspaceViewsProvider = ({ children }: { children: ReactNode }) => {
  const requester = useRequesterContext();
  const available =
    requester?.links.some(
      (named) => named.rel === "VIEW" && named.link.endpoint === "case.list",
    ) === true;
  const scope =
    requester === null
      ? "unverified"
      : JSON.stringify([requester.subject.installationId, requester.subject.actorId]);
  const query = useEndpoint("work.views", {}, available, scope);
  const observed = query.value;
  const value = useMemo<WorkspaceViews>(() => {
    const document =
      available && observed?.outcome.kind === "SUCCEEDED" ? observed.outcome.document : null;
    return {
      cases: {
        data: countData(document, "cases", caseView),
        loading: query.loading,
        message: query.message,
      },
      reminders: {
        data: countData(document, "reminders", reminderView),
        loading: query.loading,
        message: query.message,
      },
    };
  }, [available, observed, query.loading, query.message]);
  return <WorkspaceViewsContext value={value}>{children}</WorkspaceViewsContext>;
};
export const useWorkspaceViews = () => {
  const value = useContext(WorkspaceViewsContext);
  if (value === null) {
    throw new Error("View counts require the authenticated workspace owner.");
  }
  return value;
};

/** Missing view metadata is an error to recover from, never an empty result or a zero count. */
export const WorkspaceViewsStatus = ({ area }: { area: keyof WorkspaceViews }) => {
  const value = useWorkspaceViews()[area];
  const p = usePresentation();
  const cache = useEndpointCache();
  const missing = value.data === null || value.data.items.length === 0;
  if (value.loading) {
    return missing ? <p role="status">{p.text("ui.loading")}</p> : null;
  }
  if (value.message === null && !missing) {
    return null;
  }
  return (
    <div role="alert">
      <NoticeView value={value.message ?? { kind: "local", reason: "incomplete" }} />
      <button
        type="button"
        onClick={() => {
          cache?.invalidate();
        }}
      >
        {p.text("ui.tryAgain")}
      </button>
    </div>
  );
};
