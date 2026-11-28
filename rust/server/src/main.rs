mod maintenance;
use axum::serve::ListenerExt;
use std::{env, path::PathBuf};
use umd_core::CatalogStore;
use umd_server::{
    router_with_thumbnails, AccountStore, ReleaseStore, ServerState, ThumbnailCache,
    DEFAULT_MAX_PACKAGE_BYTES,
};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "umd_server=info,tower_http=info".into()),
        )
        .init();
    let directory = PathBuf::from(
        env::var("UMD_RUST_SERVER_DATA_DIRECTORY").unwrap_or_else(|_| "rust-server-data".into()),
    );
    let args: Vec<_> = env::args_os().skip(1).collect();
    if maintenance::run(&directory, &args)? {
        return Ok(());
    }
    if directory.join("server.db").try_exists()? && !directory.join("catalog.db").try_exists()? {
        return Err("Este directorio contiene un servidor .NET. Usá --migrate-dotnet ORIGEN DESTINO y arrancá Rust en el destino.".into());
    }
    std::fs::create_dir_all(&directory)?;
    let _running = maintenance::service_lock(&directory)?;
    let catalog = CatalogStore::open(directory.join("catalog.db"))?;
    let admin = umd_server::Admin::open(&directory)?;
    match env::args().skip(1).collect::<Vec<_>>().as_slice() {
        [] => (),
        [flag] if flag == "--scrape-once" => std::process::exit(admin.scrape_once(catalog).await?),
        _ => return Err("Usage: umd-server [--scrape-once]".into()),
    }
    let admin_token = env::var("UMD_SERVER_ADMIN_TOKEN")
        .ok()
        .filter(|value| !value.is_empty());
    if admin_token.as_ref().is_some_and(|token| token.len() < 32) {
        return Err("UMD_SERVER_ADMIN_TOKEN must contain at least 32 bytes".into());
    }
    let maximum_package_bytes = match env::var("UMD_SERVER_MAX_PACKAGE_BYTES") {
        Ok(raw) if !raw.trim().is_empty() => raw
            .trim()
            .parse::<u64>()
            .ok()
            .filter(|n| *n > 0 && *n <= i64::MAX as u64)
            .ok_or("UMD_SERVER_MAX_PACKAGE_BYTES must be a positive 64-bit integer")?,
        _ => DEFAULT_MAX_PACKAGE_BYTES,
    };
    let thumbnails = ThumbnailCache::from_env(&directory)?;
    let address = env::var("UMD_RUST_SERVER_LISTEN").unwrap_or_else(|_| "127.0.0.1:9005".into());
    let listener = tokio::net::TcpListener::bind(&address).await?;
    tracing::info!(address = %listener.local_addr()?, "UMD Rust catalog server started");
    admin.server_event(
        "lifecycle",
        "info",
        "started",
        "Se inició el servidor.",
        None,
    )?;
    axum::serve(
        listener.tap_io(|socket| {
            let _ = socket.set_nodelay(true);
        }),
        router_with_thumbnails(
            ServerState {
                admin: admin.clone(),
                catalog,
                admin_token,
                releases: ReleaseStore::new(&directory, maximum_package_bytes),
                accounts: AccountStore::open(directory.join("accounts.db"))?,
                allow_registration: env::var("UMD_SERVER_ALLOW_REGISTRATION")
                    .is_ok_and(|v| v == "true"),
            },
            thumbnails,
        )
        // Login throttling counts failures per client address.
        .into_make_service_with_connect_info::<std::net::SocketAddr>(),
    )
    .with_graceful_shutdown({
        let service = admin.clone();
        async move { service.shutdown_signal().await }
    })
    .await?;
    admin.finish_manual().await?;
    Ok(())
}
