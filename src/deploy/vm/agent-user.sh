#!/usr/bin/env bash
# The agent user, IN the VM, as root (vm-deploy.sh pipes it to `sudo bash -s` on every deploy).
# Agents run as arm-agent (docs/AGENT-USER.md): no sudo, no access to ARM's config, database or
# home. ARM (still `arm`) starts their supervisors through a sudoers rule for exactly the
# supervisor binary, and reads their files and sockets through the arm-agent group.
# Idempotent; the migration of a VM from before runs once (while sessions/ isn't the group's yet).
set -euo pipefail
agent=arm-agent
state=/var/lib/olve-arm
sessions=$state/sessions

id -u $agent >/dev/null 2>&1 || useradd --create-home --user-group --shell /bin/bash $agent
# ARM reads the agents' output and connects to their supervisors through the group (after its restart).
usermod -aG $agent arm
home=$(getent passwd $agent | cut -d: -f6)
chmod 0700 "$home"

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
# only ARM writes, so no agent can swap a session's folder. Existing folders keep arm as owner, so
# an agent still running as arm across this deploy keeps writing its own.
if [ -d $sessions ] && [ "$(stat -c %G $sessions)" != $agent ]; then
  echo "[agent-user] sharing the existing session folders with $agent"
  chgrp -R $agent $sessions
  chmod -R g+rwX,o-rwx $sessions
  find $sessions -type d -exec chmod g+s {} +
  # Continuing an earlier session resumes its Claude session, which Claude Code keeps in its home.
  if [ -d /home/arm/.claude/projects ] && [ ! -e "$home/.claude/projects" ]; then
    install -d -o $agent -g $agent -m 0700 "$home/.claude"
    cp -a /home/arm/.claude/projects "$home/.claude/projects"
    chown -R $agent:$agent "$home/.claude/projects"
  fi
fi
install -d -o arm -g $agent -m 2750 $sessions

# The agents' deploy key (the GIT_SSH_COMMAND variable names ~/.ssh/arm-agent-deploy, which is
# now the agent user's home): moved out of arm's.
key=/home/arm/.ssh/arm-agent-deploy
if [ -f $key ] && [ ! -f "$home/.ssh/arm-agent-deploy" ]; then
  echo "[agent-user] moving the agents' deploy key to $agent"
  install -d -o $agent -g $agent -m 0700 "$home/.ssh"
  install -o $agent -g $agent -m 0600 $key "$home/.ssh/arm-agent-deploy"
  [ ! -f $key.pub ] || install -o $agent -g $agent -m 0644 $key.pub "$home/.ssh/arm-agent-deploy.pub"
fi
[ ! -f "$home/.ssh/arm-agent-deploy" ] || rm -f $key $key.pub
