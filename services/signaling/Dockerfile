# Signaling service of the built-in classroom. Built natively on each architecture (CI: amd64 and arm64 runners).
FROM rust:1.100-slim-bookworm AS build
WORKDIR /src
# Dependencies first, cached while only the sources change.
COPY Cargo.toml Cargo.lock ./
RUN mkdir src && echo "fn main() {}" > src/main.rs && echo "" > src/lib.rs \
    && cargo build --release --locked && rm -rf src
COPY src ./src
RUN touch src/main.rs src/lib.rs && cargo build --release --locked

# glibc + CA certificates, no shell; runs as uid 65532.
FROM gcr.io/distroless/cc-debian12:nonroot
COPY --from=build /src/target/release/konnyaku-signaling /usr/local/bin/konnyaku-signaling
ENV LISTEN_ADDR=0.0.0.0:5100
EXPOSE 5100
HEALTHCHECK --interval=15s --timeout=5s --retries=3 CMD ["/usr/local/bin/konnyaku-signaling", "healthcheck"]
ENTRYPOINT ["/usr/local/bin/konnyaku-signaling"]
