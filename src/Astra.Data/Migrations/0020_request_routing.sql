-- Smart routing's decision for a request to the virtual model (.plan/smart-routing.md §5.9), as the request log shows it:
-- the grade and model it got, where the decision came from, and why a fallback grade was used (RoutingReport,
-- Json.Storage). NULL for every other request.
ALTER TABLE requests ADD COLUMN routing_json TEXT;
