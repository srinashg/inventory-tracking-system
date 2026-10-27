# shellcheck shell=bash
# Helpers for running the container tests in CI. Sourced by the test scripts; they do nothing on a developer's computer
# unless CI_LOG_DIR is set.

# ci_mask <value...>: tells GitHub Actions to replace each value with *** anywhere it appears in the job log.
# The test scripts create their own random passwords, so only they can register them.
ci_mask() {
  [ -n "${GITHUB_ACTIONS:-}" ] || return 0
  local value
  for value in "$@"; do
    [ -n "$value" ] && echo "::add-mask::$value"
  done
  return 0
}

# ci_save_logs <name> <secret...>: reads log text on stdin and, if CI_LOG_DIR is set, writes it to $CI_LOG_DIR/<name>.log with every
# secret replaced by [redacted]. (The logs are not expected to contain any secret; the tests check that. This is a second safety net.)
ci_save_logs() {
  local name=$1; shift
  if [ -z "${CI_LOG_DIR:-}" ]; then cat > /dev/null; return 0; fi
  mkdir -p "$CI_LOG_DIR"
  local args=() value
  for value in "$@"; do
    [ -n "$value" ] && args+=(-e "s/$value/[redacted]/g")
  done
  if [ "${#args[@]}" -gt 0 ]; then sed "${args[@]}" > "$CI_LOG_DIR/$name.log"; else cat > "$CI_LOG_DIR/$name.log"; fi
}
