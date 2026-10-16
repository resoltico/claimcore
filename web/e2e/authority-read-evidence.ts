import type { Page, Request, Response, TestInfo } from "@playwright/test";
import { isHostFailure, isResponse } from "@contracts/browser-contract";

type Endpoint = "context" | "authority.list";
type Advice = { kind: "GRANT" | "NO_GRANT" | "UNOBSERVED"; count: number };
type ReadEvidence = { status: number; kind: string; diagnostic: string; advice?: Advice };
export type AuthorityStage =
  | "OPEN_CASE"
  | "REGISTER_ACTOR"
  | "CASE_GRANT"
  | "DISABLE_ACTOR"
  | "NEXT_GRANT"
  | "OWNER_REFUSAL"
  | "ACCESSIBILITY";
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
      const offers =
        value.endpoint === "context" && value.outcome.tag === "SUCCEEDED"
          ? value.outcome.data.offers
          : null;
      return {
        status,
        ...(offers === null
          ? {}
          : {
              advice: {
                kind: offers.some((offer) => offer.definition === "SET_GRANT")
                  ? ("GRANT" as const)
                  : ("NO_GRANT" as const),
                count: Math.min(999, offers.length),
              },
            }),
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
  phase: string;
  picker: string;
  advice: Advice;
  stage: AuthorityStage;
  timings: Map<AuthorityStage, { elapsed: number; budget: number }>;
  clock: () => { elapsed: number; budget: number } | null;
};
const trackRequest = (state: Observation, value: Request) => {
  if (disablesActor(value)) {
    state.armed = true;
  }
  const endpoint = endpointOf(value);
  if (state.armed && endpoint !== null) {
    state.requests.set(value, endpoint);
    state.latest.set(endpoint, value);
    if (endpoint === "context") {
      state.advice = { kind: "UNOBSERVED", count: 0 };
    }
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
        if (result.advice !== undefined) {
          state.advice = result.advice;
        }
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

const authorityPicker = async (page: Page): Promise<string> => {
  try {
    const picker = page.locator(".authority-panel").getByLabel("Case action", { exact: true });
    if (!(await picker.count())) {
      return "MISSING UNOBSERVED";
    }
    const [enabled, grant] = await Promise.all([
      picker.isEnabled(),
      picker.locator('option[value="SET_GRANT"]').count(),
    ]);
    return `${enabled ? "ENABLED" : "DISABLED"} ${grant > 0 ? "GRANT" : "NO_GRANT"}`;
  } catch {
    return "UNOBSERVED UNOBSERVED";
  }
};

const recordTiming = (state: Observation) => {
  const value = state.clock();
  if (value !== null) {
    state.timings.set(state.stage, value);
  }
};
const reportFailure = async (page: Page, state: Observation) => {
  const ended = state.clock();
  const phase = await authorityPhase(page);
  const lines = [
    `E2E_AUTHORITY_PHASE ${phase === "UNAVAILABLE" ? state.phase : phase}`,
    `E2E_AUTHORITY_ADVICE ${state.advice.kind} ${state.advice.count}`,
    `E2E_AUTHORITY_PICKER ${state.picker}`,
  ];
  for (const [stage, value] of state.timings) {
    lines.push(`E2E_AUTHORITY_BODY_TIMING ${stage} ${value.elapsed} ${value.budget}`);
  }
  if (ended !== null) {
    lines.push(`E2E_AUTHORITY_BODY_TIMING FAILURE ${ended.elapsed} ${ended.budget}`);
  }
  for (const [endpoint, value] of state.evidence) {
    lines.push(
      `E2E_AUTHORITY_READ ${endpoint === "context" ? "CONTEXT" : "LIST"} ${value.status} ${value.kind} ${value.diagnostic}`,
    );
  }
  // oxlint-disable-next-line no-console -- Emit only fixed/validated failure metadata admitted by the native public sanitizer.
  console.log(lines.join("\n"));
};
const createObservation = (
  info?: Pick<TestInfo, "timeout">,
  started = performance.now(),
): Observation => ({
  evidence: new Map<Endpoint, ReadEvidence>([
    ["context", { status: 0, kind: "UNOBSERVED", diagnostic: "NONE" }],
    ["authority.list", { status: 0, kind: "UNOBSERVED", diagnostic: "NONE" }],
  ]),
  requests: new WeakMap(),
  latest: new Map(),
  armed: false,
  closed: false,
  phase: "UNAVAILABLE",
  picker: "UNOBSERVED UNOBSERVED",
  advice: { kind: "UNOBSERVED", count: 0 },
  stage: "OPEN_CASE",
  timings: new Map(),
  clock: () =>
    info === undefined
      ? null
      : {
          elapsed: Math.min(9999999, Math.max(0, Math.floor(performance.now() - started))),
          budget: Math.min(9999999, Math.max(0, Math.floor(info.timeout))),
        },
});

/** Observe only fresh sibling reads after this scenario sends its service-disable request. */
export const observeAuthorityRefresh = (page: Page, info?: Pick<TestInfo, "timeout">) => {
  const state = createObservation(info);
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
  return {
    stage: async (stage: AuthorityStage) => {
      state.stage = stage;
      recordTiming(state);
      if (stage === "REGISTER_ACTOR" || stage === "NEXT_GRANT") {
        const [phase, picker] = await Promise.all([authorityPhase(page), authorityPicker(page)]);
        if (phase !== "UNAVAILABLE") {
          state.phase = phase;
        }
        state.picker = picker;
        recordTiming(state);
      }
    },
    finish: async (report: boolean) => {
      state.closed = true;
      page.off("request", request);
      page.off("response", response);
      page.off("requestfailed", failed);
      if (report) {
        await reportFailure(page, state);
      }
    },
  };
};
