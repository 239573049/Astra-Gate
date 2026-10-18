# my zshrc
export PATH="$HOME/bin:$PATH"
alias ll="ls -la"
# >>> astra muse-code >>>
# Muse Code is routed through Astra. Turn the Muse Code client off in Astra to remove this block.
muse() {
  case "${1-}" in
    exec)
      shift
      META_API_KEY='sk-astra'\''second' MUSE_MODEL='deepseek-v4-pro' command muse exec --base-url 'https://astra.corp.com/v1' "$@"
      ;;
    resume|export|trace|skills|sandbox|session-message|auth|login|logout|init|config|help)
      command muse "$@"
      ;;
    *)
      META_API_KEY='sk-astra'\''second' MUSE_MODEL='deepseek-v4-pro' command muse --base-url 'https://astra.corp.com/v1' "$@"
      ;;
  esac
}
# <<< astra muse-code <<<
