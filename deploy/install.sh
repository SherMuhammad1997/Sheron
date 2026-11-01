#!/usr/bin/env bash
# Installs or updates Sheron's server on Oracle Linux 9 (Plan step 13; TRD › Server › Deployment).
# Put Sheron.Server, sheron-server.service and this file in one folder on the server, then run:
#   sudo bash install.sh
# Safe to run again for an update: the data folder (TLS key and ID registry) is kept.
set -euo pipefail
cd "$(dirname "$0")"

if [ "$(id -u)" -ne 0 ]; then echo "Run it with sudo: sudo bash install.sh"; exit 1; fi
for f in Sheron.Server sheron-server.service; do
  if [ ! -f "$f" ]; then echo "$f must be in this folder, next to install.sh."; exit 1; fi
done

# The server's own user: no login, no shell, no home of its own.
id sheron >/dev/null 2>&1 || useradd --system --no-create-home --home-dir /opt/sheron-server --shell /sbin/nologin sheron

systemctl stop sheron-server 2>/dev/null || true
install -d -o root -g root -m 755 /opt/sheron-server
install -d -o sheron -g sheron -m 700 /opt/sheron-server/data
install -o root -g root -m 755 Sheron.Server /opt/sheron-server/Sheron.Server
install -o root -g root -m 644 sheron-server.service /etc/systemd/system/sheron-server.service
# SELinux is on by default: give the new files their normal labels so systemd may start the program.
if command -v restorecon >/dev/null; then restorecon -R /opt/sheron-server /etc/systemd/system/sheron-server.service; fi

# This machine's own firewall: TCP and UDP 443 in. (Oracle Cloud's network rules are opened in its console.)
if systemctl is-active --quiet firewalld; then
  firewall-cmd --quiet --permanent --add-port=443/tcp --add-port=443/udp
  firewall-cmd --quiet --reload
fi

systemctl daemon-reload
systemctl enable --quiet sheron-server
systemctl start sheron-server
sleep 3
systemctl --no-pager --lines=0 status sheron-server || true
echo
echo "Server log (the app needs the pin shown here):"
journalctl -u sheron-server --no-pager -n 4 -o cat
