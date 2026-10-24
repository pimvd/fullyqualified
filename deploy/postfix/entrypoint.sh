#!/bin/sh
set -eu

POSTFIX_HOSTNAME=${POSTFIX_HOSTNAME:-mail.fullyqualified.invalid}
POSTFIX_MYNETWORKS=${POSTFIX_MYNETWORKS:-127.0.0.0/8}

# Treat environment values as data, never as shell commands or additional settings.
case "$POSTFIX_HOSTNAME" in
    *[!a-zA-Z0-9.-]*|.*|*..*|*.)
        echo 'POSTFIX_HOSTNAME must be a fully qualified DNS hostname.' >&2
        exit 1
        ;;
esac
case "$POSTFIX_HOSTNAME" in
    *.*) ;;
    *) echo 'POSTFIX_HOSTNAME must be a fully qualified DNS hostname.' >&2; exit 1 ;;
esac
case "$POSTFIX_MYNETWORKS" in
    *[!0-9./\ ,]*)
        echo 'POSTFIX_MYNETWORKS must contain only IPv4 CIDR ranges.' >&2
        exit 1
        ;;
esac
case "$POSTFIX_HOSTNAME" in
    *.invalid) echo 'Set FQ_MAIL_HOSTNAME to a public hostname before sending external mail.' >&2 ;;
esac

if [ -n "${POSTFIX_RELAY_HOST:-}" ]; then
    case "$POSTFIX_RELAY_HOST" in
        *[!a-zA-Z0-9.-]*) echo 'POSTFIX_RELAY_HOST must be a DNS hostname.' >&2; exit 1 ;;
    esac
    (umask 077; printf '[%s]:587 %s:%s\n' "$POSTFIX_RELAY_HOST" "${POSTFIX_RELAY_USER:-}" "${POSTFIX_RELAY_PASSWORD:-}" > /etc/postfix/sasl_passwd)
    postconf -e "relayhost = [$POSTFIX_RELAY_HOST]:587"
fi

postconf -e "myhostname = $POSTFIX_HOSTNAME" "mynetworks = $POSTFIX_MYNETWORKS"
postfix check
exec postfix start-fg
