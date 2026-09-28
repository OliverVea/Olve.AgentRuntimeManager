#!/usr/bin/env bash
# The agent user, IN the VM, as root (vm-deploy.sh pipes it to `sudo bash -s` on every deploy).
# Agents run as arm-agent (docs/AGENT-USER.md): no sudo, no access to ARM's config, database or
# home. ARM (still `arm`) starts their supervisors through a sudoers rule for exactly the
# supervisor binary, and reads their files and sockets through the arm-agent group.
# Every step is idempotent on its own and runs on every deploy; what agents still running as arm
# (from before the agent user) keep making is carried over again each time.
set -euo pipefail
cd /
agent=arm-agent
state=/var/lib/olve-arm
sessions=$state/sessions

id -u $agent >/dev/null 2>&1 || useradd --create-home --user-group --shell /bin/bash $agent
# ARM reads the agents' output and connects to their supervisors through the group (after its restart).
usermod -aG $agent arm
home=$(getent passwd $agent | cut -d: -f6)
chmod 0700 "$home"

# Whatever goes into the agent's home is written AS the agent: it owns that home, and root
# following a link it put there would write (or chown) anything. Each such step is also bounded
# (the agent can make any file there a FIFO, which blocks its reader or writer) and best effort
# (a dotfile it broke must not stop every deploy): a failure or a timeout is a warning.
step_timeout=${AGENT_STEP_TIMEOUT:-30}
as_agent() { runuser -u $agent -- timeout -k 5 "$step_timeout" env HOME="$home" "$@"; }
warn() { echo "[agent-user] warning: $*" >&2; }

rules=$(mktemp)
cat > "$rules" <<RULES
# ARM (user arm) starts each session's supervisor as the agent user, and kills an orphaned run's
# agent as that user (docs/AGENT-USER.md). Nothing else. Managed by vm-deploy.sh.
arm ALL=($agent) NOPASSWD: /opt/olve-arm/releases/*/olve-arm-supervisor, /opt/olve-arm/current/olve-arm-supervisor
RULES
visudo -cqf "$rules"
install -m 0440 "$rules" /etc/sudoers.d/olve-arm-agent
rm -f "$rules"

# The database only for arm (the service's UMask keeps new files so; SQLite gives its WAL the
# database's mode). The state folder can be passed through, not listed.
install -d -o arm -g arm -m 0711 $state
for db in "$state"/arm.db*; do
  [ ! -e "$db" ] || chmod 0600 "$db"
done

# The sessions (the agents' workplaces and output): each session's folder is shared with the agent
# user's group (ARM makes it so), setgid so what's made in it stays the group's; sessions/ itself
# only ARM writes, so no agent can swap a session's folder. The session folders from before the
# agent user are shared once; sessions/ turning the group's (last) marks that as done. They keep
# arm as owner, so an agent still running as arm across this deploy keeps writing its own.
# chgrp/chmod -R don't follow links (-P), find doesn't either.
if [ -d $sessions ] && [ "$(stat -c %G $sessions)" != $agent ]; then
  echo "[agent-user] sharing the existing session folders with $agent"
  find $sessions -mindepth 1 -maxdepth 1 -exec chgrp -R $agent {} + -exec chmod -R g+rwX,o-rwx {} +
  find $sessions -mindepth 1 -type d -exec chmod g+s {} +
fi
install -d -o arm -g $agent -m 2750 $sessions

# What agents still running as arm (from before the agent user) keep making, carried over to the
# agent user on every deploy. Best effort: run as `f || warn`, where set -e doesn't stop them.

# Git refuses a repository another user owns ("dubious ownership"): those an agent made as arm
# are made safe for the agent, one by one. git 2.43 has no prefix patterns for safe.directory
# (2.46 does), and '*' would trust any owner's repository. Only workplaces from before the agent
# user are arm's (a new one is the agent's own, made by its supervisor), and a repository sits
# near a workplace's top: the walk is bounded however much an agent puts in its workplace.
trust_arms_repositories() {
  command -v git >/dev/null || return 0
  local -A known=()
  local entry work repo entries status=0
  # NUL-separated (a path may hold a newline). 1 is "none yet"; anything else (a config it broke,
  # a FIFO timing out) skips the step, rather than every repository waiting out its own timeout.
  entries=$(mktemp)
  as_agent git config --global -z --get-all safe.directory > "$entries" || status=$?
  if [ $status -gt 1 ]; then
    rm -f "$entries"
    warn "$agent's git config can't be read ($status): arm's repositories not trusted"
    return 0
  fi
  while IFS= read -r -d '' entry; do
    [ -z "$entry" ] || known[$entry]=1
  done < "$entries"
  rm -f "$entries"
  for work in "$sessions"/*/work; do
    [ -d "$work" ] && [ ! -L "$work" ] && [ "$(stat -c %U "$work")" = arm ] || continue
    while IFS= read -r -d '' repo; do
      [ "$(stat -c %U "$repo")" = arm ] && [ -z "${known[$repo]:-}" ] || continue
      as_agent git config --global --add safe.directory "$repo" || warn "could not trust $repo for $agent's git"
      known[$repo]=1
    done < <(find "$work" -maxdepth 3 \( -name node_modules -prune \) -o \( -name .git -prune -printf '%h\0' \))
  done
}

# Continuing an earlier session resumes its Claude session, which Claude Code keeps in its home:
# the transcripts arm's agents wrote, newer ones over older (an agent still running as arm keeps
# writing its own). Read as root, written as the agent.
sync_claude_sessions() {
  [ -d /home/arm/.claude/projects ] || return 0
  local copy
  as_agent mkdir -p -m 0700 "$home/.claude/projects" \
    && copy=$(as_agent mktemp -d "$home/.claude/.from-arm.XXXXXX") && [ -n "$copy" ] \
    || { warn "no place for arm's Claude sessions in $agent's home"; return 0; }
  tar -C /home/arm/.claude -cf - projects | as_agent tar -C "$copy" -xf - \
    && as_agent cp -a -u "$copy/projects/." "$home/.claude/projects/" \
    || warn "could not carry over all of arm's Claude sessions"
  as_agent rm -rf "$copy" || warn "could not remove $copy"
}

# The agents' deploy key (the GIT_SSH_COMMAND variable names ~/.ssh/arm-agent-deploy, which is
# now the agent user's home), and GitHub's host key. arm keeps its copy of the key while agents
# from before may still run as arm (a later cleanup, docs/AGENT-USER.md). Whatever is already in
# the key's place stays; known_hosts is only read and added to while it's a plain file (or none).
copy_ssh() {
  local key=/home/arm/.ssh/arm-agent-deploy
  as_agent mkdir -p -m 0700 "$home/.ssh" || { warn "no ~/.ssh for $agent"; return 0; }
  if [ -f $key ] && ! as_agent test -e "$home/.ssh/arm-agent-deploy"; then
    echo "[agent-user] copying the agents' deploy key to $agent"
    as_agent sh -c 'umask 077 && cat > "$HOME/.ssh/arm-agent-deploy"' < $key || warn "could not copy the deploy key"
    [ ! -f $key.pub ] || as_agent sh -c 'cat > "$HOME/.ssh/arm-agent-deploy.pub"' < $key.pub || warn "could not copy the deploy key's .pub"
  fi
  [ -f /home/arm/.ssh/known_hosts ] || return 0
  if ! as_agent sh -c 'f="$HOME/.ssh/known_hosts"; [ ! -e "$f" ] && [ ! -L "$f" ] || { [ -f "$f" ] && [ ! -L "$f" ]; }'; then
    warn "$agent's known_hosts isn't a plain file: GitHub's host key not added"
  elif ! as_agent ssh-keygen -F github.com -f "$home/.ssh/known_hosts" >/dev/null 2>&1; then
    ssh-keygen -F github.com -f /home/arm/.ssh/known_hosts | grep -v '^#' \
      | as_agent sh -c 'umask 077 && cat >> "$HOME/.ssh/known_hosts"' || warn "could not add GitHub's host key"
  fi
}

trust_arms_repositories || warn "could not trust arm's repositories for $agent's git"
sync_claude_sessions || warn "could not carry over arm's Claude sessions"
copy_ssh || warn "could not carry over the deploy key"
