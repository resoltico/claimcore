import { test, type Page, type Request, type Response } from "@playwright/test";
import { isWebV3Response } from "../src/generated/contracts/web-v3.validation";
import type { WebV3Response } from "../src/generated/contracts/web-v3.types";

type RequestClass = "document" | "script" | "stylesheet" | "session" | "definition";
type RequestEvidence = {
  kind: RequestClass;
  status: number | null;
  phase: "pending" | "headers" | "finished" | "failed";
  elapsedMs: number;
};

const classify = (request: Request): RequestClass | null => {
  const path = new URL(request.url()).pathname;
  if (path === "/api/v3/session") {
    return "session";
  }
  if (path === "/api/v3/definition") {
    return "definition";
  }
  const kind = request.resourceType();
  return kind === "document" || kind === "script" || kind === "stylesheet" ? kind : null;
};

const readiness = async (page: Page) => {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      page
        .evaluate(() => {
          const value = document.querySelector("main")?.className;
          return {
            readyState: document.readyState,
            view:
              value === "loading" || value === "login-shell" || value === "app-shell"
                ? value
                : "other",
            alert: document.querySelector('[role="alert"]') !== null,
          };
        })
        .catch(() => null),
      new Promise<null>((resolve) => {
        timer = setTimeout(() => {
          resolve(null);
        }, 250);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
};

const elapsed = (started: number): number =>
  Math.min(60_000, Math.max(0, Math.round(performance.now() - started)));

class StartupEvidence {
  readonly records = new Map<Request, { started: number; evidence: RequestEvidence }>();
  truncated = false;
  authenticated: boolean | null = null;
  pageErrorSeen = false;
  readonly page: Page;

  constructor(page: Page) {
    this.page = page;
    page.on("request", this.begin);
    page.on("response", this.received);
    page.on("requestfinished", this.finished);
    page.on("requestfailed", this.failed);
    page.on("pageerror", this.pageError);
  }

  readonly begin = (request: Request) => {
    const kind = classify(request);
    if (kind === null) {
      return;
    }
    if (this.records.size >= 40) {
      this.truncated = true;
      return;
    }
    this.records.set(request, {
      started: performance.now(),
      evidence: { kind, status: null, phase: "pending", elapsedMs: 0 },
    });
  };

  update(request: Request, phase: RequestEvidence["phase"], status?: number) {
    const record = this.records.get(request);
    if (record !== undefined) {
      record.evidence.phase = phase;
      record.evidence.elapsedMs = elapsed(record.started);
      if (status !== undefined) {
        record.evidence.status = status;
      }
    }
  }

  async readSession(value: Response) {
    const payload: unknown = await value.json();
    if (await isWebV3Response("session", payload)) {
      this.authenticated = (payload as WebV3Response<"session">).outcome.data.authenticated;
    }
  }

  readonly received = (value: Response) => {
    this.update(value.request(), "headers", value.status());
    if (classify(value.request()) === "session" && value.status() === 200) {
      void this.readSession(value).catch(() => undefined);
    }
  };
  readonly finished = (request: Request) => {
    this.update(request, "finished");
  };
  readonly failed = (request: Request) => {
    this.update(request, "failed");
  };
  readonly pageError = () => {
    this.pageErrorSeen = true;
  };

  async capture() {
    await test.info().attach("claimcore-startup", {
      contentType: "application/json",
      body: Buffer.from(
        JSON.stringify({
          truncated: this.truncated,
          authenticated: this.authenticated,
          pageErrorSeen: this.pageErrorSeen,
          readiness: await readiness(this.page),
          requests: [...this.records.values()].map(({ evidence, started }) => ({
            kind: evidence.kind,
            status: evidence.status,
            phase: evidence.phase,
            elapsedMs:
              evidence.phase === "pending" || evidence.phase === "headers"
                ? elapsed(started)
                : evidence.elapsedMs,
          })),
        }),
      ),
    });
  }

  dispose() {
    this.page.off("request", this.begin);
    this.page.off("response", this.received);
    this.page.off("requestfinished", this.finished);
    this.page.off("requestfailed", this.failed);
    this.page.off("pageerror", this.pageError);
  }
}

// Capture only public status and fixed readiness classes at the failed startup boundary.
// URLs, bodies, selectors, cookies, provider errors and visible text are never retained.
export const observeStartup = async (page: Page, action: () => Promise<void>): Promise<void> => {
  const evidence = new StartupEvidence(page);
  try {
    await action();
  } catch (error) {
    // Diagnostic retention must never replace the original navigation/assertion failure.
    await evidence.capture().catch(() => undefined);
    throw error;
  } finally {
    evidence.dispose();
  }
};
