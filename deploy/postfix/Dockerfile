FROM debian:trixie-slim

RUN printf '#!/bin/sh\nexit 101\n' > /usr/sbin/policy-rc.d \
    && chmod 755 /usr/sbin/policy-rc.d \
    && apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends postfix ca-certificates libsasl2-modules \
    && rm -rf /var/lib/apt/lists/* \
    && postconf -M | awk '{ print $1 "/" $2 "/chroot=n" }' | while read -r field; do postconf -F "$field"; done

COPY main.cf /etc/postfix/main.cf
COPY entrypoint.sh /usr/local/bin/postfix-entrypoint
RUN chmod 755 /usr/local/bin/postfix-entrypoint

EXPOSE 25
STOPSIGNAL SIGTERM
ENTRYPOINT ["/usr/local/bin/postfix-entrypoint"]
