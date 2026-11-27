import { Button } from "../ui/Button";
import { ActionMenu } from "../ui/ActionMenu";
import { useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import type { ExportRequest } from "@contracts/client";
import { routeRevisionSnapshot, subscribeRoute } from "../routing/navigation";
import { useDisplayGeneration, useEndpointCache } from "../hooks/EndpointProvider";
import { useSettingsDetour } from "../hooks/SettingsDetourContext";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { useSessionContext } from "../session/SessionContext";
import { requestCsvExport, csvExportNotice, saveCsvDownload } from "../api/csvExport";
import { sameOwnerScope, type OwnerScope } from "../protocol/ownerScope";
import { localNotice, type Notice } from "../protocol/notices";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";

const exportOwner = (
  state: ReturnType<typeof useSessionContext>["state"],
  requester: ReturnType<typeof useRequesterContext>,
): OwnerScope | null =>
  state.kind === "authenticated" && requester !== null
    ? {
        serviceOrigin: window.location.origin,
        installationId: requester.subject.installationId,
        actorId: requester.subject.actorId,
        sessionEpoch: state.epoch,
      }
    : null;

export const CsvExportButton = ({
  selection,
  available = true,
  label = "label.exportCsv",
  menu,
}: {
  selection: ExportRequest;
  available?: boolean;
  menu?: { label: string; report: string; onReport: () => void };
  label?: "label.exportCsv" | "ui.exportCasesCsv" | "ui.exportReportCsv";
}) => {
  const p = usePresentation();
  const cache = useEndpointCache();
  const display = useDisplayGeneration();
  const requester = useRequesterContext();
  const { state } = useSessionContext();
  const owner = exportOwner(state, requester);
  const routeRevision = useSyncExternalStore(subscribeRoute, routeRevisionSnapshot);
  const detour = useSettingsDetour();
  const offered = available && detour?.paused !== true;
  const admitted = offered && owner !== null;
  const credential = state.kind === "authenticated" ? state.token : "";
  const scope = JSON.stringify([owner, credential, selection, offered, routeRevision, display]);
  const current = useRef({ owner, scope });
  const active = useRef<{ scope: string; controller: AbortController } | null>(null);
  const downloadUrl = useRef<{ scope: string; dispose: () => void } | null>(null);
  const [view, setView] = useState<{ scope: string; busy: boolean; notice: Notice | null }>({
    scope,
    busy: false,
    notice: null,
  });
  useLayoutEffect(() => {
    current.current = { owner, scope };
  });
  useLayoutEffect(
    () => () => {
      if (active.current?.scope === scope) {
        active.current.controller.abort();
        active.current = null;
      }
      if (downloadUrl.current?.scope === scope) {
        downloadUrl.current.dispose();
        downloadUrl.current = null;
      }
    },
    [scope],
  );
  const complete = (
    result: Awaited<ReturnType<typeof requestCsvExport>>,
    selectedOwner: OwnerScope,
    currentOwner: () => OwnerScope | null,
  ) => {
    let notice = csvExportNotice(result);
    if (result.kind === "outcome" && result.value.kind === "DOWNLOAD") {
      downloadUrl.current?.dispose();
      try {
        downloadUrl.current = {
          scope,
          dispose: saveCsvDownload(result.value.document, selectedOwner, currentOwner),
        };
      } catch {
        notice = localNotice("downloadFailed");
      }
    }
    setView({ scope, busy: false, notice });
  };
  const download = async () => {
    if (active.current !== null && active.current.scope !== scope) {
      active.current.controller.abort();
      active.current = null;
    }
    if (!admitted || active.current !== null) {
      return;
    }
    const currentOwner = () =>
      cache?.admitsDisplay(display) === true &&
      routeRevisionSnapshot() === routeRevision &&
      current.current.scope === scope
        ? current.current.owner
        : null;
    const request = { scope, controller: new AbortController() };
    active.current = request;
    setView({ scope, busy: true, notice: null });
    const result = await requestCsvExport(
      selection,
      credential,
      owner,
      currentOwner,
      request.controller.signal,
    );
    if (active.current !== request) {
      return;
    }
    active.current = null;
    if (request.controller.signal.aborted || !sameOwnerScope(owner, currentOwner())) {
      return;
    }
    complete(result, owner, currentOwner);
  };
  const disabled = !admitted || (view.scope === scope && view.busy);
  return (
    <div className="csv-export">
      {menu === undefined ? (
        <Button
          variant="secondary"
          isDisabled={disabled}
          onPress={() => {
            void download();
          }}
        >
          {p.text(label)}
        </Button>
      ) : (
        <ActionMenu
          variant="secondary"
          compact={false}
          label={menu.label}
          items={[
            {
              id: "csv",
              label: p.text(label),
              disabled,
              select: () => {
                void download();
              },
            },
            { id: "report", label: menu.report, select: menu.onReport },
          ]}
        />
      )}
      {view.scope === scope && view.notice !== null ? (
        <p role="alert">
          <NoticeView value={view.notice} />
        </p>
      ) : null}
    </div>
  );
};
