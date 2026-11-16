# >>> astra antigravity-cli >>>
# Antigravity CLI is routed through Astra. Turn the Antigravity CLI client off in Astra to remove this block.
unalias agy 2>/dev/null || true
agy() {
  local m a
  m='astra/gpt-5'
  for a in "$@"; do case "$a" in remote-control|--remote-control|--remote-control=*|-remote-control) command agy "$@"; return;; esac; done
  for a in "$@"; do case "$a" in --model|--model=*|-model|-model=*) m=;; esac; done
  if [ -n "$m" ]; then set -- --model "$m" "$@"; fi
  set -- --settings "$HOME/.gemini/antigravity-cli/astra-settings.json" "$@"
  GEMINI_API_KEY='sk-astra-enterprise-key' GOOGLE_GEMINI_BASE_URL='https://astra.corp.com' command agy "$@"
}
# <<< astra antigravity-cli <<<
