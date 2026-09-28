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
# following a link it put there would write (or chown) anything.
as_agent() { runuser -u $agent -- env HOME="$home" "$@"; }

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

# Git refuses a repository another user owns ("dubious ownership"): those an agent made as arm
# (in session folders from before) are made safe for the agent, one by one. git 2.43 has no
# prefix patterns for safe.directory (2.46 does), and '*' would trust any owner's repository.
if command -v git >/dev/null; then
  known=$(as_agent git config --global --get-all safe.directory || true)
  while IFS= read -r -d '' repo; do
    [ "$(stat -c %U "$repo")" = arm ] || continue
    grep -qxF "$repo" <<<"$known" || as_agent git config --global --add safe.directory "$repo"
  done < <(find $sessions -mindepth 2 \( -name node_modules -prune \) -o \( -name .git -prune -printf '%h\0' \))
fi

# Continuing an earlier session resumes its Claude session, which Claude Code keeps in its home:
# the transcripts arm's agents wrote, newer ones over older (an agent still running as arm keeps
# writing its own). Read as root, written as the agent.
if [ -d /home/arm/.claude/projects ]; then
  as_agent mkdir -p -m 0700 "$home/.claude/projects"
  copy=$(as_agent mktemp -d "$home/.claude/.from-arm.XXXXXX")
  tar -C /home/arm/.claude -cf - projects | as_agent tar -C "$copy" -xf - \
    && as_agent cp -a -u "$copy/projects/." "$home/.claude/projects/" \
    || echo "[agent-user] could not carry over all of arm's Claude sessions" >&2
  as_agent rm -rf "$copy"
fi

# The agents' deploy key (the GIT_SSH_COMMAND variable names ~/.ssh/arm-agent-deploy, which is
# now the agent user's home), and GitHub's host key. arm keeps its copy of the key while agents
# from before may still run as arm (a later cleanup, docs/AGENT-USER.md).
key=/home/arm/.ssh/arm-agent-deploy
as_agent mkdir -p -m 0700 "$home/.ssh"
if [ -f $key ] && ! as_agent test -f "$home/.ssh/arm-agent-deploy"; then
  echo "[agent-user] copying the agents' deploy key to $agent"
  as_agent sh -c 'umask 077 && cat > "$HOME/.ssh/arm-agent-deploy"' < $key
  [ ! -f $key.pub ] || as_agent sh -c 'cat > "$HOME/.ssh/arm-agent-deploy.pub"' < $key.pub
fi
if [ -f /home/arm/.ssh/known_hosts ] && ! as_agent ssh-keygen -F github.com -f "$home/.ssh/known_hosts" >/dev/null 2>&1; then
  ssh-keygen -F github.com -f /home/arm/.ssh/known_hosts | grep -v '^#' \
    | as_agent sh -c 'umask 077 && cat >> "$HOME/.ssh/known_hosts"' || true
fi
