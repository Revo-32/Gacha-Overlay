# Build context is the repository root. The adjacent Dockerfile.dockerignore
# admits only these static files and the exact, immutable logo source.
FROM busybox:1.37.0
COPY web/status/public/ /www/
COPY assets/branding/LS_Overlay_logo.png /www/assets/ls-overlay-logo.png
COPY web/status/entrypoint.sh /entrypoint.sh
USER 10001:10001
EXPOSE 8080
STOPSIGNAL SIGTERM
ENTRYPOINT ["/bin/sh", "/entrypoint.sh"]
