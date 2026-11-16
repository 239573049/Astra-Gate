# my zshrc
export PATH="$HOME/bin:$PATH"
alias ll="ls -l"
# >>> astra dim >>>
# Dim is routed through Astra. Turn the Dim client off in Astra to remove this block.
unalias dima 2>/dev/null || true
dima() {
  DIM_BASE_URL='https://astra.corp.com/v1' DIM_API_FORMAT=openai-compatible DIM_API_KEY='sk-astra-enterprise-key' DIM_MODEL='gpt-5' DIM_MAX_CONTEXT_TOKENS=400000 DIM_MAX_OUTPUT_TOKENS=128000 command dima "$@"
}
# <<< astra dim <<<
