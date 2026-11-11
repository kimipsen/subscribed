FROM mcr.microsoft.com/devcontainers/dotnet:2-10.0-noble

ARG TARGETARCH
ARG DOTNET_SDK_VERSION=11.0.100
ARG NODE_VERSION=24.22.2
ARG DOCKER_CLI_VERSION=5:29.9.3-1~ubuntu.24.04~noble
ARG COMPOSE_VERSION=5.7.0-1~ubuntu.24.04~noble

RUN test "${TARGETARCH:-$(dpkg --print-architecture)}" = amd64 \
    || { echo "This toolchain is verified for amd64 only." >&2; exit 1; }

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates curl gnupg xz-utils libpython3.12-stdlib \
    && install -m 0755 -d /etc/apt/keyrings \
    && curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc \
    && gpg --show-keys --with-colons /etc/apt/keyrings/docker.asc \
        | grep -q '^fpr:::::::::9DC858229FC7DD38854AE2D88D81803C0EBFCD88:' \
    && chmod a+r /etc/apt/keyrings/docker.asc \
    && printf '%s\n' \
        'Types: deb' \
        'URIs: https://download.docker.com/linux/ubuntu' \
        'Suites: noble' \
        'Components: stable' \
        'Architectures: amd64' \
        'Signed-By: /etc/apt/keyrings/docker.asc' \
        > /etc/apt/sources.list.d/docker.sources \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        "docker-ce-cli=${DOCKER_CLI_VERSION}" \
        "docker-compose-plugin=${COMPOSE_VERSION}" \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL \
        "https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/dotnet-sdk-${DOTNET_SDK_VERSION}-linux-x64.tar.gz" \
        -o /tmp/dotnet-sdk.tar.gz \
    && echo '1bec9cca2ec689b1e80245b84f2b53c9441d1aa6daccf2ceb27490768bffa95954fa585bd275def31069d874d543d9ba9e6591fe9ade2dbd9f34ec5c0fd0050c  /tmp/dotnet-sdk.tar.gz' \
        | sha512sum -c - \
    && mkdir -p /opt/dotnet11 \
    && tar -xzf /tmp/dotnet-sdk.tar.gz -C /opt/dotnet11 \
    && rm /tmp/dotnet-sdk.tar.gz

RUN curl -fsSL \
        "https://nodejs.org/dist/v${NODE_VERSION}/node-v${NODE_VERSION}-linux-x64.tar.xz" \
        -o /tmp/node.tar.xz \
    && echo '48cc0e21e048790e54c0cf5749c2725005313d652651c40b7a21feb9697aaf75  /tmp/node.tar.xz' \
        | sha256sum -c - \
    && mkdir -p /opt/node \
    && tar -xJf /tmp/node.tar.xz --strip-components=1 -C /opt/node \
    && rm /tmp/node.tar.xz

ENV DOTNET_ROOT=/opt/dotnet11
ENV PATH="/opt/dotnet11:/opt/node/bin:${PATH}"

RUN test "$(command -v dotnet)" = /opt/dotnet11/dotnet \
    && test "$(dotnet --version)" = "${DOTNET_SDK_VERSION}" \
    && test "$(node --version)" = "v${NODE_VERSION}" \
    && test "$(npm --version)" = 11.19.1 \
    && docker --version \
    && docker compose version \
    && python3 -I -c 'import json, encodings, ssl, sqlite3'