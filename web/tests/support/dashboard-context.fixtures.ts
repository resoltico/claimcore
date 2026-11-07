import { workViewsFixture } from "./view-counts.fixtures";
import { actorContextInput, workBasis } from "./inputs";
import { mockEndpoint } from "./endpoint-mocks";
export const stubViewCounts = () => {
  mockEndpoint("work.views").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { kind: "SUCCEEDED", data: workViewsFixture } },
  });
};
export const stubDashboardContext = () => {
  stubViewCounts();
  return mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "SUCCEEDED",
        data: {
          requester: actorContextInput({
            productVersion: "0.10.0",
            businessDate: workBasis.businessDate,
            timeZone: "Etc/UTC",
            capabilities: ["REGISTER"],
            views: ["CASES", "AUTHORITY", "OPERATIONS"],
            offers: [{ kind: "OPEN" }],
          }),
        },
      },
    },
  });
};
