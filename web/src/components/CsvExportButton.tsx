import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { ExportRequest } from "@contracts/client";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { useSessionContext } from "../session/SessionContext";
import { requestCsvExport, csvExportNotice, saveCsvDownload } from "../api/csvExport";
import { sameOwnerScope, type OwnerScope } from "../protocol/ownerScope";
import { localNotice, type Notice } from "../protocol/notices";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";

export const CsvExportButton = ({
  selection,
  available = true,
}: {
  selection: ExportRequest;
  available?: boolean;
}) => {
  const p = usePresentation();
  const requester = useRequesterContext();
  const { state } = useSessionContext();
  const owner: OwnerScope | null =
    state.kind === "authenticated" && requester !== null
      ? {
          serviceOrigin: window.location.origin,
          installationId: requester.subject.installationId,
          actorId: requester.subject.actorId,
          sessionEpoch: state.epoch,
        }
      : null;
  const credential = state.kind === "authenticated" ? state.token : "";
  const scope = JSON.stringify([owner, selection, available]);
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
  useEffect(
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
    if (owner === null || !available || active.current !== null) {
      return;
    }
    const currentOwner = () => (current.current.scope === scope ? current.current.owner : null);
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
  return (
    <div className="csv-export">
      <button
        type="button"
        disabled={!available || owner === null || (view.scope === scope && view.busy)}
        onClick={() => {
          void download();
        }}
      >
        {p.text("label.exportCsv")}
      </button>
      {view.scope === scope && view.notice !== null ? (
        <p role="alert">
          <NoticeView value={view.notice} />
        </p>
      ) : null}
    </div>
  );
};
