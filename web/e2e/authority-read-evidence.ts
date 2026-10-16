import type { Page, Request, Response } from "@playwright/test";
import { isHostFailure, isResponse } from "@contracts/browser-contract";

type Endpoint = "context" | "authority.list";
type ReadEvidence = { status: number; kind: string; diagnostic: string };
const endpointOf = (request: Request): Endpoint | null => {
  const path = new URL(request.url()).pathname;
  if (path === "/api/context") {
    return "context";
  }
  return path === "/api/authority.list" ? "authority.list" : null;
};
const disablesActor = (request: Request): boolean => {
  if (new URL(request.url()).pathname !== "/api/authority.execute") {
    return false;
  }
  try {
    const input: unknown = request.postDataJSON();
    return (
      typeof input === "object" &&
      input !== null &&
      "act" in input &&
      typeof input.act === "object" &&
      input.act !== null &&
      "kind" in input.act &&
      input.act.kind === "SET_ENABLED" &&
      "enabled" in input.act &&
      input.act.enabled === false
    );
  } catch {
    return false;
  }
};
const responseEvidence = async (endpoint: Endpoint, response: Response): Promise<ReadEvidence> => {
  const status = response.status();
  try {
    const value: unknown = await response.json();
    if (isResponse(endpoint, value)) {
      const { outcome } = value;
      return {
        status,
        kind: outcome.tag,
        diagnostic:
          outcome.tag === "REJECTED" || outcome.tag === "FAILED"
            ? outcome.data.diagnostic.id
            : "NONE",
      };
    }
    if (isHostFailure(value, status)) {
      return { status, kind: "HOST_FAILURE", diagnostic: value.diagnostic.id };
    }
  } catch {
    // A failed diagnostic read does not replace the scenario's original failure.
  }
  return { status, kind: "INVALID", diagnostic: "NONE" };
};

type Observation = {
  evidence: Map<Endpoint, ReadEvidence>;
  requests: WeakMap<Request, Endpoint>;
  latest: Map<Endpoint, Request>;
  armed: boolean;
  closed: boolean;
};
const trackRequest = (state: Observation, value: Request) => {
  if (disablesActor(value)) {
    state.armed = true;
  }
  const endpoint = endpointOf(value);
  if (state.armed && endpoint !== null) {
    state.requests.set(value, endpoint);
    state.latest.set(endpoint, value);
    state.evidence.set(endpoint, { status: 0, kind: "PENDING", diagnostic: "NONE" });
  }
};
const trackResponse = (state: Observation, value: Response) => {
  const endpoint = state.requests.get(value.request());
  if (endpoint !== undefined && state.latest.get(endpoint) === value.request()) {
    state.evidence.set(endpoint, { status: value.status(), kind: "PENDING", diagnostic: "NONE" });
    void responseEvidence(endpoint, value).then((result) => {
      if (!state.closed && state.latest.get(endpoint) === value.request()) {
        state.evidence.set(endpoint, result);
      }
    });
  }
};
const trackFailure = (state: Observation, value: Request) => {
  const endpoint = state.requests.get(value);
  if (endpoint !== undefined && state.latest.get(endpoint) === value) {
    state.evidence.set(endpoint, { status: 0, kind: "TRANSPORT", diagnostic: "NONE" });
  }
};

const authorityPhase = async (page: Page): Promise<string> => {
  try {
    const panel = page.locator(".authority-panel");
    if (await page.getByRole("dialog").count()) {
      return "DISCARD_CONFIRMATION";
    }
    if (await panel.getByRole("heading", { name: "Review exact request", exact: true }).count()) {
      return "REVIEW";
    }
    if (await panel.getByRole("alert").count()) {
      return "FAILURE";
    }
    const picker = panel.getByLabel("Case action", { exact: true });
    if (
      (await picker.count()) &&
      (await picker.locator("option").count()) &&
      (await picker.isEnabled())
    ) {
      return "USABLE_PICKER";
    }
    if (
      await panel
        .getByRole("status")
        .filter({ hasText: /^Loading/u })
        .count()
    ) {
      return "LOADING";
    }
    return "EMPTY_ADVICE";
  } catch {
    return "UNAVAILABLE";
  }
};

/** Observe only fresh sibling reads after this scenario sends its service-disable request. */
export const observeAuthorityRefresh = (page: Page) => {
  const state: Observation = {
    evidence: new Map<Endpoint, ReadEvidence>([
      ["context", { status: 0, kind: "UNOBSERVED", diagnostic: "NONE" }],
      ["authority.list", { status: 0, kind: "UNOBSERVED", diagnostic: "NONE" }],
    ]),
    requests: new WeakMap(),
    latest: new Map(),
    armed: false,
    closed: false,
  };
  const request = (value: Request) => {
    trackRequest(state, value);
  };
  const response = (value: Response) => {
    trackResponse(state, value);
  };
  const failed = (value: Request) => {
    trackFailure(state, value);
  };
  page.on("request", request);
  page.on("response", response);
  page.on("requestfailed", failed);
  return async (report: boolean) => {
    state.closed = true;
    page.off("request", request);
    page.off("response", response);
    page.off("requestfailed", failed);
    if (report) {
      // oxlint-disable-next-line no-console -- Emit only fixed failure metadata admitted by the native public sanitizer.
      console.log(`E2E_AUTHORITY_PHASE ${await authorityPhase(page)}`);
      for (const [endpoint, value] of state.evidence) {
        // oxlint-disable-next-line no-console -- Emit only validated read metadata admitted by the native public sanitizer.
        console.log(
          `E2E_AUTHORITY_READ ${endpoint === "context" ? "CONTEXT" : "LIST"} ${value.status} ${value.kind} ${value.diagnostic}`,
        );
      }
    }
  };
};
