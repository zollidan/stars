FROM rust:1.98-slim-trixie AS builder
WORKDIR /src
# Зависимости отдельным слоем: правка кода не пересобирает весь граф крейтов.
COPY Cargo.toml Cargo.lock ./
RUN mkdir src && echo 'fn main() {}' > src/main.rs \
    && cargo build --release --locked && rm -rf src
COPY src ./src
COPY migrations ./migrations
RUN touch src/main.rs && cargo build --release --locked

FROM debian:trixie-slim
COPY --from=builder /src/target/release/stars /usr/local/bin/stars
USER nobody
EXPOSE 8080
CMD ["stars"]
