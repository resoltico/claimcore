import { AnnotationMode, getDocument, PDFWorker, type RenderTask } from "pdfjs-dist";
import workerSource from "virtual:attachment-preview-worker";
import { attachmentTransport } from "@contracts/client";
import { isPositiveInteger, messageRecord, previewChannel } from "./channel";

const pixels = attachmentTransport.maximumPreviewPixels;
const canvas = document.querySelector("canvas")!;

const pageCanvas = (width: number, height: number) => {
  if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) {
    throw new Error("Invalid PDF page geometry.");
  }
  const singleSide = Math.floor(Math.sqrt(pixels));
  const scale = Math.min(
    (window.innerWidth * window.devicePixelRatio) / width,
    (window.innerHeight * window.devicePixelRatio) / height,
    singleSide / width,
    singleSide / height,
  );
  const result = document.createElement("canvas");
  result.width = Math.max(1, Math.floor(width * scale));
  result.height = Math.max(1, Math.floor(height * scale));
  return { result, scale };
};

const initialize = (port: MessagePort, bytes: ArrayBuffer) => {
  let request = 0;
  const resources = new Map<number, (bytes: Uint8Array<ArrayBuffer>) => void>();
  let resource = 0;
  const fetchResource = (resourceType: "cmaps" | "wasm", name: string) => {
    const resourceId = ++resource;
    return new Promise<Uint8Array<ArrayBuffer>>((resolve) => {
      resources.set(resourceId, resolve);
      port.postMessage({ kind: "resource", resourceId, resourceType, name });
    });
  };
  class PreviewBinaryDataFactory {
    fetch({ kind, filename }: { kind: string; filename: string }) {
      switch (kind) {
        case "cMapUrl":
          return fetchResource("cmaps", filename);
        case "wasmUrl":
          return fetchResource("wasm", filename);
        default:
          throw new Error("Unsupported PDF resource.");
      }
    }
  }
  // The blob contains the static renderer program. PDF bytes travel only over the ports.
  const workerUrl = URL.createObjectURL(new Blob([workerSource], { type: "text/javascript" }));
  const worker = new Worker(workerUrl);
  const releaseUrl = () => {
    URL.revokeObjectURL(workerUrl);
  };
  worker.addEventListener("message", releaseUrl, { once: true });
  window.addEventListener(
    "pagehide",
    () => {
      releaseUrl();
      worker.terminate();
    },
    { once: true },
  );
  const loading = getDocument({
    data: bytes,
    worker: PDFWorker.create({ port: worker }),
    BinaryDataFactory: PreviewBinaryDataFactory,
    cMapPacked: true,
    useWasm: true,
    useWorkerFetch: false,
    disableWorkerRendering: true,
    useSystemFonts: true,
    enableXfa: false,
    stopAtErrors: true,
    maxImageSize: pixels,
    canvasMaxAreaInBytes: pixels * 4,
  });
  let active: RenderTask | null = null;
  let visible = canvas;
  const draw = async (requestId: number, page: number) => {
    request = requestId;
    active?.cancel();
    const pdf = await loading.promise;
    if (requestId !== request || page > pdf.numPages) {
      return;
    }
    const pageData = await pdf.getPage(page);
    if (requestId !== request) {
      return;
    }
    const { width, height } = pageData.getViewport({ scale: 1 });
    const { result, scale } = pageCanvas(width, height);
    const render = pageData.render({
      canvas: result,
      viewport: pageData.getViewport({ scale }),
      // Paint static appearances into the canvas without creating an interactive layer.
      annotationMode: AnnotationMode.ENABLE,
    });
    active = render;
    try {
      await render.promise;
      if (requestId === request) {
        visible.replaceWith(result);
        visible = result;
        port.postMessage({ requestId, page, pageCount: pdf.numPages });
      }
    } finally {
      if (active === render) {
        active = null;
      }
    }
  };
  port.onmessage = ({ data }: MessageEvent<unknown>) => {
    if (
      messageRecord(data, ["requestId", "page"]) &&
      isPositiveInteger(data["requestId"]) &&
      data["requestId"] > request &&
      isPositiveInteger(data["page"])
    ) {
      // Rendering failures leave the parent deadline in charge of removing the frame.
      void draw(data["requestId"], data["page"]).catch(() => undefined);
    } else if (
      messageRecord(data, ["resourceId", "bytes"]) &&
      isPositiveInteger(data["resourceId"]) &&
      data["bytes"] instanceof ArrayBuffer
    ) {
      resources.get(data["resourceId"])?.(new Uint8Array(data["bytes"]));
      resources.delete(data["resourceId"]);
    }
  };
};

// The parent sends one port to this opaque-origin document, then uses the port exclusively.
const acceptChannel = (event: MessageEvent<unknown>) => {
  if (
    event.source !== parent ||
    event.origin !== location.origin ||
    event.data !== previewChannel ||
    event.ports.length !== 1
  ) {
    return;
  }
  const port = event.ports[0]!;
  window.removeEventListener("message", acceptChannel);
  port.onmessage = ({ data }: MessageEvent<unknown>) => {
    if (data instanceof ArrayBuffer) {
      initialize(port, data);
    }
  };
};
window.addEventListener("message", acceptChannel);
