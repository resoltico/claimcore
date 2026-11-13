import { humanReadHttpReply as response } from "../support/human-read-reply.fixtures";
import { endpointResponse } from "../support/transport-response.fixtures";
import { timelineDocument } from "../support/timeline-document.fixtures";
import {
  caseDocumentInput,
  caseIdentityScreen,
  closeCaseScreen,
} from "../support/case-document.fixtures";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { CaseDetail } from "../../src/views/CaseDetail";
import { definition, fields } from "../support/service-ui.fixtures";
const context = caseDocumentInput(
  { tasks: ["case.close"], history: "FULL" },
  {
    regions: [
      ...closeCaseScreen.regions.filter((region) => region.role !== "IDENTITY"),
      ...caseIdentityScreen.regions,
      {
        role: "FACTS",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "claimantName",
              labelId: "field.claimantName.label",
              prominence: "PRIMARY",
              value: { kind: "PARTY", value: fields.registration.claimantName },
            },
          ],
        },
      },
    ],
  },
);
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
              ? endpointResponse("case.timeline", {
                  representation: "HUMAN",
                  outcome: {
                    kind: "REJECTED",
                    rejection: {
                      kind: "REFUSED",
                      advice: {
                        message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
                        action: "ASK_FOR_ACCESS",
                      },
                    },
                  },
                })
              : sessionRefusal(),
          );
        }
        return Promise.resolve(
          response("case.timeline", timelineDocument([], null, revoked ? "SUMMARY" : "FULL")),
        );
      }
      return Promise.resolve(
        revoked && !retainCurrent
          ? endpointResponse("case.get", {
              representation: "HUMAN",
              outcome: {
                kind: "REJECTED",
                rejection: {
                  kind: "REFUSED",
                  advice: {
                    message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
                    action: "ASK_FOR_ACCESS",
                  },
                },
              },
            })
          : response("case.get", {
              ...context,
              context: {
                ...context.context,
                history: revoked ? "SUMMARY" : "FULL",
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
      await user.click(screen.getByRole("tab", { name: "Case facts" }));
      await waitFor(() => {
        expect(revoked()).toBe(true);
        expect(
          document.querySelectorAll('[data-region="FACTS"] [data-entry="claimantName"]'),
        ).toHaveLength(retainCurrent ? 1 : 0);
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
        expect(screen.getByRole("tab", { name: "Everything" })).toHaveAttribute(
          "aria-selected",
          "true",
        );
        expect(screen.getByRole("tab", { name: "Case facts" })).toHaveAttribute(
          "aria-selected",
          "false",
        );
      } else {
        expect(screen.queryByText(fields.registration.claimantName)).toBeNull();
      }
    },
  );
}
