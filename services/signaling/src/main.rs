use konnyaku_signaling::{Config, Reporter, app, bus, server};
use tracing_subscriber::EnvFilter;

#[tokio::main]
async fn main() {
    // `konnyaku-signaling healthcheck`: container health probe (the runtime image has no shell or curl).
    if std::env::args().nth(1).as_deref() == Some("healthcheck") {
        std::process::exit(if healthcheck() { 0 } else { 1 });
    }

    tracing_subscriber::fmt()
        .with_env_filter(EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info")))
        // Colors only on a terminal, not in container logs.
        .with_ansi(std::io::IsTerminal::is_terminal(&std::io::stdout()))
        .init();

    let config = match Config::from_env() {
        Ok(config) => config,
        Err(message) => {
            eprintln!("configuration error: {message}");
            std::process::exit(2);
        }
    };
    let reporter = Reporter::start(config.api_url.clone(), config.secret.clone());
    if config.api_url.is_none() {
        tracing::warn!("API_URL is not set: presence is not reported (no attendance from the built-in classroom)");
    }
    let addr = config.listen_addr.clone();
    let capacity = config.capacity;
    let redis_url = config.redis_url.clone();
    let (router, state) = app(config, reporter);
    match redis_url {
        Some(url) => {
            tokio::spawn(bus::run(url, state.hub.clone(), state.redis_up.clone()));
        }
        None => tracing::warn!("REDIS_URL is not set: realtime sockets (/rt) get no signals (browsers poll instead)"),
    }
    tokio::spawn(server::sweeper(state));

    let listener = tokio::net::TcpListener::bind(&addr)
        .await
        .unwrap_or_else(|e| panic!("cannot listen on {addr}: {e}"));
    tracing::info!(%addr, capacity, "signaling and realtime listening");
    axum::serve(listener, router)
        .with_graceful_shutdown(shutdown())
        .await
        .expect("server");
}

async fn shutdown() {
    let ctrl_c = async {
        let _ = tokio::signal::ctrl_c().await;
    };
    #[cfg(unix)]
    let terminate = async {
        if let Ok(mut s) = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate()) {
            s.recv().await;
        }
    };
    #[cfg(not(unix))]
    let terminate = std::future::pending::<()>();
    tokio::select! { _ = ctrl_c => {}, _ = terminate => {} }
}

fn healthcheck() -> bool {
    use std::io::{Read, Write};
    let addr = std::env::var("LISTEN_ADDR").unwrap_or_else(|_| "0.0.0.0:5100".into());
    let port = addr.rsplit(':').next().unwrap_or("5100");
    let Ok(mut stream) = std::net::TcpStream::connect(format!("127.0.0.1:{port}")) else {
        return false;
    };
    let _ = stream.set_read_timeout(Some(std::time::Duration::from_secs(3)));
    let mut response = String::new();
    let request = b"GET /health HTTP/1.0\r\nHost: localhost\r\n\r\n";
    if stream.write_all(request).is_err() || stream.read_to_string(&mut response).is_err() {
        return false;
    }
    response.split(' ').nth(1) == Some("200")
}
