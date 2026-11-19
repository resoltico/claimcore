import { useCallback, useLayoutEffect, useRef, useState } from "react";
import { attachmentTransport } from "@contracts/client";
import { readAttachmentPreviewResource } from "../../../api/attachments";
import { usePresentation } from "../../../presentation/context";
import { isPositiveInteger, messageRecord, previewChannel } from "./channel";

const pageDeadline = 20_000;
type Position = { page: number; pageCount: number };
type Connection = {
  port: MessagePort;
  requests: AbortController;
  requestId: number;
  page: number;
  pageCount: number | null;
  awaiting: boolean;
};

type ResourceRequest = { id: number; type: "cmaps" | "wasm"; name: string };
const resourceRequest = (message: unknown): ResourceRequest | null => {
  if (
    !messageRecord(message, ["kind", "resourceId", "resourceType", "name"]) ||
    message["kind"] !== "resource" ||
    !isPositiveInteger(message["resourceId"]) ||
    (message["resourceType"] !== "cmaps" && message["resourceType"] !== "wasm") ||
    typeof message["name"] !== "string"
  ) {
    return null;
  }
  return { id: message["resourceId"], type: message["resourceType"], name: message["name"] };
};

const sendResource = async (connection: Connection, { id, type, name }: ResourceRequest) => {
  const { signal } = connection.requests;
  const bytes = await readAttachmentPreviewResource(type, name, signal);
  if (bytes !== null && !signal.aborted) {
    connection.port.postMessage({ resourceId: id, bytes }, [bytes]);
  }
};

export const DocumentPreview = ({
  bytes,
  title,
  unavailable,
}: {
  bytes: ArrayBuffer;
  title: string;
  unavailable: string;
}) => {
  const p = usePresentation();
  const session = useRef<Connection | null>(null);
  const clock = useRef(0);
  const [position, setPosition] = useState<Position | null>(null);
  const [requested, setRequested] = useState(1);
  const [failed, setFailed] = useState(false);

  const release = useCallback(() => {
    window.clearTimeout(clock.current);
    session.current?.requests.abort();
    session.current?.port.close();
    session.current = null;
  }, []);
  const fail = useCallback(() => {
    release();
    setFailed(true);
  }, [release]);
  useLayoutEffect(() => {
    clock.current = window.setTimeout(fail, pageDeadline);
    return release;
  }, [fail, release]);

  const navigate = (offset: number) => {
    const connection = session.current;
    if (connection === null || connection.pageCount === null) {
      return;
    }
    const page = connection.page + offset;
    if (page < 1 || page > connection.pageCount) {
      return;
    }
    connection.page = page;
    connection.requestId++;
    connection.awaiting = true;
    window.clearTimeout(clock.current);
    clock.current = window.setTimeout(fail, pageDeadline);
    setRequested(page);
    connection.port.postMessage({ requestId: connection.requestId, page });
  };

  const start = (frame: HTMLIFrameElement) => {
    const viewer = frame.contentWindow;
    if (viewer === null || session.current !== null) {
      fail();
      return;
    }
    const channel = new MessageChannel();
    const connection: Connection = {
      port: channel.port1,
      requests: new AbortController(),
      requestId: 1,
      page: 1,
      pageCount: null,
      awaiting: true,
    };
    session.current = connection;
    connection.port.onmessage = ({ data }: MessageEvent<unknown>) => {
      if (session.current !== connection) {
        return;
      }
      const resource = resourceRequest(data);
      if (resource !== null) {
        void sendResource(connection, resource).catch(() => undefined);
        return;
      }
      if (
        connection.awaiting &&
        messageRecord(data, ["requestId", "page", "pageCount"]) &&
        data["requestId"] === connection.requestId &&
        data["page"] === connection.page &&
        isPositiveInteger(data["pageCount"]) &&
        data["pageCount"] >= connection.page &&
        (connection.pageCount === null || data["pageCount"] === connection.pageCount)
      ) {
        connection.awaiting = false;
        connection.pageCount = data["pageCount"];
        window.clearTimeout(clock.current);
        setPosition({ page: connection.page, pageCount: connection.pageCount });
      }
    };
    viewer.postMessage(previewChannel, "*", [channel.port2]);
    connection.port.postMessage(bytes, [bytes]);
    connection.port.postMessage({ requestId: connection.requestId, page: connection.page });
  };

  if (failed) {
    return <p role="alert">{unavailable}</p>;
  }
  return (
    <>
      {position === null ? null : (
        <div className="attachment-actions">
          <button
            type="button"
            disabled={requested <= 1}
            onClick={() => {
              navigate(-1);
            }}
          >
            {p.text("ui.previousPage")}
          </button>
          <button
            type="button"
            disabled={requested >= position.pageCount}
            onClick={() => {
              navigate(1);
            }}
          >
            {p.text("ui.nextPage")}
          </button>
        </div>
      )}
      <p role="status" aria-live="polite">
        {position === null
          ? p.text("ui.loading")
          : p.text("ui.documentPage", {
              page: p.integer(position.page),
              count: p.integer(position.pageCount),
            })}
      </p>
      <iframe
        className="attachment-document"
        src={attachmentTransport.viewerPath}
        sandbox="allow-scripts"
        tabIndex={-1}
        title={title}
        onLoad={(event) => {
          start(event.currentTarget);
        }}
      />
    </>
  );
};
