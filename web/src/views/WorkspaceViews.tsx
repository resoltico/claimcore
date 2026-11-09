import { useEndpointCache } from "../hooks/EndpointProvider";
import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import { createContext, useContext, useMemo, type ReactNode } from "react";
import type {
  HumanReadDocument_workViews,
  PresentedValue,
  Screen,
  WorkDateBasis,
} from "@contracts/client";
import type { Notice } from "../protocol/notices";
import { useEndpoint } from "../hooks/useEndpoint";
import { useRequesterContext } from "../hooks/useRequesterContext";
type Views<T> = { data: T | null; loading: boolean; message: Notice | null };
type ViewCounts<V> = {
  basis: WorkDateBasis;
  navigation: PresentedValue | null;
  items: ReadonlyArray<{ view: V; label: PresentedValue; count: PresentedValue }>;
};
export type CaseCounts = ViewCounts<
  HumanReadDocument_workViews["context"]["cases"][number]["view"]
>;
export type ReminderCounts = ViewCounts<
  HumanReadDocument_workViews["context"]["reminders"][number]["view"]
>;
const viewChoices = <V extends string>(
  screen: Screen,
  controls: ReadonlyArray<{ key: string; view: V }>,
): ViewCounts<V>["items"] | null => {
  const tables = screen.regions.flatMap((region) =>
    region.role === "RESULTS" && region.content.kind === "TABLE" ? [region.content] : [],
  );
  const items: Array<{ view: V; label: PresentedValue; count: PresentedValue }> = [];
  for (const control of controls) {
    const table = tables.find((table) => table.rows.some((row) => row.key === control.key));
    const row = table?.rows.find((row) => row.key === control.key);
    const label = row?.values[table?.columns.findIndex((column) => column.key === "view") ?? -1];
    const count = row?.values[table?.columns.findIndex((column) => column.key === "count") ?? -1];
    if (label === undefined || count === undefined) {
      return null;
    }
    items.push({ view: control.view, label, count });
  }
  return items;
};
export type WorkspaceViews = { cases: Views<CaseCounts>; reminders: Views<ReminderCounts> };
export const WorkspaceViewsContext = createContext<WorkspaceViews | null>(null);
/** One authenticated shell owns these snapshot counts; renderers never define their predicates. */
export const WorkspaceViewsProvider = ({ children }: { children: ReactNode }) => {
  const requester = useRequesterContext();
  const available = requester?.context.views.includes("CASES") === true;
  const scope =
    requester === null
      ? "unverified"
      : JSON.stringify([requester.context.installationId, requester.context.actorId]);
  const query = useEndpoint("work.views", {}, available, scope);
  const observed = query.value;
  const value = useMemo<WorkspaceViews>(() => {
    const document =
      available && observed?.outcome.kind === "SUCCEEDED" ? observed.outcome.document : null;
    const cases = document === null ? null : viewChoices(document.screen, document.context.cases);
    const reminders =
      document === null ? null : viewChoices(document.screen, document.context.reminders);
    const basis = document?.basis.kind === "SNAPSHOT" ? document.basis.observation : null;
    const navigation =
      document?.screen.regions.flatMap((region) =>
        region.role === "TOTALS" && region.content.kind === "ENTRIES" ? region.content.entries : [],
      ) ?? [];
    return {
      cases: {
        data:
          basis === null || cases === null
            ? null
            : {
                basis,
                items: cases,
                navigation: navigation.find((entry) => entry.key === "cases")?.value ?? null,
              },
        loading: query.loading,
        message: query.message,
      },
      reminders: {
        data:
          basis === null || reminders === null
            ? null
            : {
                basis,
                items: reminders,
                navigation: navigation.find((entry) => entry.key === "reminders")?.value ?? null,
              },
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
