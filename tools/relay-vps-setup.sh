#!/usr/bin/env bash
# Prepares the VPS for the SessionDeck relay tunnel. Run ON the server as a sudoer:
#   bash relay-vps-setup.sh "ssh-ed25519 AAAA... sessiondeck-tunnel@talon"
# Idempotent: the user, key, sshd drop-in and Caddy block are each created once.
set -euo pipefail

PUBKEY="${1:?public key line required}"
USER_NAME=sdtunnel
LISTEN=127.0.0.1:8103
HOSTNAME_PUBLIC=sessiondeck.0a.gg
CADDYFILE=/etc/caddy/Caddyfile

if ! id -u "$USER_NAME" >/dev/null 2>&1; then
  sudo useradd --system --shell /usr/sbin/nologin --create-home "$USER_NAME"
  echo "created user $USER_NAME"
fi

HOME_DIR="$(getent passwd "$USER_NAME" | cut -d: -f6)"
sudo install -d -m 700 -o "$USER_NAME" -g "$USER_NAME" "$HOME_DIR/.ssh"
printf 'restrict,port-forwarding,permitlisten="%s" %s\n' "$LISTEN" "$PUBKEY" | sudo tee "$HOME_DIR/.ssh/authorized_keys" >/dev/null
sudo chown "$USER_NAME:$USER_NAME" "$HOME_DIR/.ssh/authorized_keys"
sudo chmod 600 "$HOME_DIR/.ssh/authorized_keys"
echo "authorized_keys written"

DROPIN=/etc/ssh/sshd_config.d/60-sdtunnel.conf
sudo tee "$DROPIN" >/dev/null <<EOF
Match User $USER_NAME
    AllowTcpForwarding remote
    PermitTTY no
    X11Forwarding no
    ClientAliveInterval 30
    ClientAliveCountMax 3
EOF
sudo sshd -t
sudo systemctl reload ssh
echo "sshd drop-in in place"

if ! grep -q "^$HOSTNAME_PUBLIC" "$CADDYFILE"; then
  sudo cp "$CADDYFILE" "$CADDYFILE.bak.relay-$(date +%Y%m%d-%H%M%S)"
  sudo tee -a "$CADDYFILE" >/dev/null <<EOF

$HOSTNAME_PUBLIC {
	encode zstd gzip
	reverse_proxy $LISTEN
	handle_errors {
		@down expression {err.status_code} == 502
		respond @down "SessionDeck is offline" 503
	}
}
EOF
  cp "$CADDYFILE" /tmp/Caddyfile.check
  sudo -u caddy caddy validate --config /tmp/Caddyfile.check --adapter caddyfile
  sudo systemctl reload caddy
  echo "caddy block added and reloaded"
else
  echo "caddy block already present"
fi

sleep 2
echo "health from the outside: $(curl -s -o /dev/null -w '%{http_code}' "https://$HOSTNAME_PUBLIC/health")"
