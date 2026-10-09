# syntax=docker/dockerfile:1
#
# Astra server image (astra-server + web UI) — assembled from the artifacts the
# release already builds, so the image runs the very same Native AOT binary as
# the npm `@aidotnet/server-linux-*` packages. Nothing is compiled here.
#
# Expected build context layout (see Dockerfile.dockerignore):
#   docker-bin/amd64/   contents of npm/server-linux-x64/bin    (astra-server, libe_sqlite3.so, wwwroot/)
#   docker-bin/arm64/   contents of npm/server-linux-arm64/bin
#
# Local build (after pack-platform.mjs produced npm/server-linux-x64/bin):
#   mkdir -p docker-bin && cp -R npm/server-linux-x64/bin docker-bin/amd64
#   docker build -t astra .
# Run:
#   docker run -d -p 17321:17321 -v astra-data:/data -e ASTRA_ADMIN_PASSWORD=change-me-please astra
#
# glibc base on purpose: the release RIDs are linux-x64 / linux-arm64 (glibc), not linux-musl-*.
# runtime-deps ships the libs a self-contained binary needs (OpenSSL, ICU) and a non-root `app` user (uid 1654).
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0

ARG TARGETARCH
WORKDIR /app
COPY docker-bin/${TARGETARCH}/ /app/

# /data holds everything stateful (SQLite DB, DataProtection keys in keys/, logs, backups). It is created
# here, owned by `app`, so a fresh named volume inherits writable ownership. Keys and DB must stay together:
# without keys/ the stored provider API keys cannot be decrypted.
COPY --chown=app:app docker/data /data

ENV ASTRA_HOME=/data \
    ASTRA_HOST=0.0.0.0 \
    ASTRA_PORT=17321 \
    ASTRA_STRICT_PORT=1
# Listening off-loopback requires an admin password: pass ASTRA_ADMIN_PASSWORD (or ASTRA_ADMIN_PASSWORD_FILE).

USER app
VOLUME ["/data"]
EXPOSE 17321

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["/app/astra-server", "healthcheck"]

# Exec form: the server is PID 1 and receives SIGTERM for a graceful shutdown.
ENTRYPOINT ["/app/astra-server"]
CMD ["serve"]
