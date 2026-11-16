-- The context window a request filled (ContextReport, Json.Storage): the model's window and max output at request time and an
-- estimate of what the prompt was made of (system / tools / history / latest turn). NULL for rows logged before this.
ALTER TABLE requests ADD COLUMN context_json TEXT;
