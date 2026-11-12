import { workViewsDocument } from "./view-counts.fixtures";
import { workBasis } from "./inputs";
import { activeRequester } from "./human-requester.fixtures";
import { mockEndpoint } from "./endpoint-mocks";
export const stubViewCounts = () => {
  mockEndpoint("work.views").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      representation: "HUMAN",
      outcome: { kind: "SUCCEEDED", document: workViewsDocument() },
    },
  });
};
export const stubDashboardContext = () => {
  stubViewCounts();
  return mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      representation: "HUMAN",
      outcome: {
        kind: "SUCCEEDED",
        document: { ...activeRequester, basis: { kind: "SNAPSHOT", observation: workBasis } },
      },
    },
  });
};
