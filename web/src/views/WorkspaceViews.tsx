import { useEndpointCache } from "../hooks/EndpointProvider";
import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import { createContext, useContext, useMemo, type ReactNode } from "react";
import type { WorkViews } from "@contracts/client";
import type { Notice } from "../protocol/notices";
import { useEndpoint } from "../hooks/useEndpoint";
import { useRequesterContext } from "../hooks/useRequesterContext";
type Views<T> = { data: T | null; loading: boolean; message: Notice | null };
export type CaseCounts = { basis: WorkViews["basis"]; items: WorkViews["caseViews"] };
export type ReminderCounts = { basis: WorkViews["basis"]; items: WorkViews["reminderViews"] };
export type WorkspaceViews = { cases: Views<CaseCounts>; reminders: Views<ReminderCounts> };
export const WorkspaceViewsContext = createContext<WorkspaceViews | null>(null);
/** One authenticated shell owns these snapshot counts; renderers never define their predicates. */
export const WorkspaceViewsProvider = ({ children }: { children: ReactNode }) => {
  const requester = useRequesterContext();
  const available = requester?.views.includes("CASES") === true;
  const scope =
    requester === null
      ? "unverified"
      : JSON.stringify([requester.installationId, requester.actorId]);
  const query = useEndpoint("work.views", {}, available, scope);
  const observed = query.value ?? (query.loading ? query.previous : null);
  const value = useMemo<WorkspaceViews>(() => {
    const data = available && observed?.outcome.kind === "SUCCEEDED" ? observed.outcome.data : null;
    return {
      cases: {
        data: data === null ? null : { basis: data.basis, items: data.caseViews },
        loading: query.loading,
        message: query.message,
      },
      reminders: {
        data: data === null ? null : { basis: data.basis, items: data.reminderViews },
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
