// Independent literal two-group patch oracles; never derived from product metadata or encoding.
export const correctionSerializations = [
  [
    "KEEP",
    "KEEP",
    '{"operationId":"00000000-0000-4000-8000-000000000001","task":"case.correct","binding":{"kind":"CASE","target":{"kind":"REFERENCE","caseReference":"CASE-SERIALIZATION"},"expectedRevision":4},"inputs":{"group:registration":"KEEP","group:decision":"KEEP"}}',
  ],
  [
    "KEEP",
    "UPDATE",
    '{"operationId":"00000000-0000-4000-8000-000000000001","task":"case.correct","binding":{"kind":"CASE","target":{"kind":"REFERENCE","caseReference":"CASE-SERIALIZATION"},"expectedRevision":4},"inputs":{"group:registration":"KEEP","group:decision":"UPDATE","field:paymentDecisionDate":"2026-09-03","field:payableAmount":"2.0000","field:payableCurrency":"EUR"}}',
  ],
  [
    "KEEP",
    "CLEAR",
    '{"operationId":"00000000-0000-4000-8000-000000000001","task":"case.correct","binding":{"kind":"CASE","target":{"kind":"REFERENCE","caseReference":"CASE-SERIALIZATION"},"expectedRevision":4},"inputs":{"group:registration":"KEEP","group:decision":"CLEAR"}}',
  ],
  [
    "UPDATE",
    "KEEP",
    '{"operationId":"00000000-0000-4000-8000-000000000001","task":"case.correct","binding":{"kind":"CASE","target":{"kind":"REFERENCE","caseReference":"CASE-SERIALIZATION"},"expectedRevision":4},"inputs":{"group:registration":"UPDATE","field:claimantName":"Synthetic replacement","field:claimedAmount":"1.0000","field:claimedCurrency":"EUR","group:decision":"KEEP"}}',
  ],
  [
    "UPDATE",
    "UPDATE",
    '{"operationId":"00000000-0000-4000-8000-000000000001","task":"case.correct","binding":{"kind":"CASE","target":{"kind":"REFERENCE","caseReference":"CASE-SERIALIZATION"},"expectedRevision":4},"inputs":{"group:registration":"UPDATE","field:claimantName":"Synthetic replacement","field:claimedAmount":"1.0000","field:claimedCurrency":"EUR","group:decision":"UPDATE","field:paymentDecisionDate":"2026-09-03","field:payableAmount":"2.0000","field:payableCurrency":"EUR"}}',
  ],
  [
    "UPDATE",
    "CLEAR",
    '{"operationId":"00000000-0000-4000-8000-000000000001","task":"case.correct","binding":{"kind":"CASE","target":{"kind":"REFERENCE","caseReference":"CASE-SERIALIZATION"},"expectedRevision":4},"inputs":{"group:registration":"UPDATE","field:claimantName":"Synthetic replacement","field:claimedAmount":"1.0000","field:claimedCurrency":"EUR","group:decision":"CLEAR"}}',
  ],
] as const;
