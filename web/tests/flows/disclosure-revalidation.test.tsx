import { timelinePage } from "../support/timeline.fixtures";
import { caseViewInput } from "../support/inputs";
import { caseAdvice } from "../support/case-advice.fixtures";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { CaseDetail } from "../../src/views/CaseDetail";
import { definition, fields, response, unavailable } from "../support/service-ui.fixtures";
const context = caseViewInput({
  caseId: "20000000-0000-4000-8000-000000000001",
  caseReference: fields.caseReference,
  revision: 1,
  advice: {
    ...caseAdvice(["CLOSE"]),
    history: "FULL",
  },
  state: {
    kind: "ACTIVE",
    fields,
  },
});
const sessionRefusal = () =>
  new Response(
    JSON.stringify({
      hostFailure: {
        code: "SESSION_REJECTED",
        phase: "NOT_STARTED",
      },
    }),
    {
      status: 401,
      headers: {
        "content-type": "application/json",
      },
    },
  );
const stubReads = (kind: string, retainCurrent: boolean) => {
  let revoked = false;
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockImplementation((url, init) => {
      if (typeof url !== "string" || typeof init?.body !== "string") {
        throw new Error("Missing synthetic read.");
      }
      if (url.endsWith("case.timeline")) {
        const input = JSON.parse(init.body) as {
          filter?: string;
        };
        if (input.filter === "FACTS") {
          revoked = true;
          return Promise.resolve(
            kind === "access"
              ? response("case.timeline", "REJECTED", unavailable)
              : sessionRefusal(),
          );
        }
        return Promise.resolve(response("case.timeline", "SUCCEEDED", timelinePage()));
      }
      return Promise.resolve(
        revoked && !retainCurrent
          ? response("case.get", "REJECTED", unavailable)
          : response("case.get", "SUCCEEDED", {
              ...context,
              advice: {
                ...context.advice,
                history: revoked ? ["SUMMARY"] : ["SUMMARY", "FULL"],
              },
            }),
      );
    }),
  );
  return () => revoked;
};
for (const retainCurrent of [false, true]) {
  it.each(["access", "session"])(
    `revalidates case disclosure after %s history refusal with current read ${retainCurrent}`,
    async (kind) => {
      const revoked = stubReads(kind, retainCurrent);
      const user = userEvent.setup();
      render(
        <CaseDetail
          target={{ kind: "REFERENCE", caseReference: fields.caseReference }}
          reloadSignal={0}
          definition={definition.catalog}
          onBack={vi.fn()}
          onAct={vi.fn()}
        />,
      );
      await screen.findAllByText(fields.registration.claimantName);
      await user.click(screen.getByRole("button", { name: "Facts" }));
      await waitFor(() => {
        expect(revoked()).toBe(true);
        expect(document.querySelectorAll(".case-fields")).toHaveLength(retainCurrent ? 1 : 0);
        expect(
          screen.queryByRole("option", {
            name: "Full snapshots",
          }),
        ).toBeNull();
      });
      await waitFor(() => {
        expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledTimes(retainCurrent ? 5 : 4);
      });
      if (retainCurrent) {
        expect(screen.getByRole("button", { name: "Everything" })).toHaveAttribute(
          "aria-pressed",
          "true",
        );
        expect(screen.getByRole("button", { name: "Facts" })).toHaveAttribute(
          "aria-pressed",
          "false",
        );
      } else {
        expect(screen.queryByText(fields.registration.claimantName)).toBeNull();
      }
    },
  );
}
