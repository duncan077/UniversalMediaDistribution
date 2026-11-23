use axum::{
    body::{to_bytes, Body},
    http::{header, Request, StatusCode},
    response::Response,
    Router,
};
use base64::{engine::general_purpose::STANDARD, Engine};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use tempfile::TempDir;
use tower::ServiceExt;
use umd_core::CatalogStore;
use umd_server::{router, AccountStore, ReleaseStore, ServerState};

const TOKEN: &str = "release-admin-token-for-local-tests";
const PACKAGE: &str = "/api/v1/releases/Stable/WIN-x64/v1.2/package/UMD.zip";
const NORMALIZED: &str = "/api/v1/releases/stable/win-x64/1.2/package/UMD.zip";
const LATEST: &str = "/api/v1/releases/stable/win-x64/latest";
fn app(dir: &TempDir, admin: bool) -> Router {
    router(ServerState {
        admin: umd_server::Admin::open(dir.path()).unwrap(),
        catalog: CatalogStore::in_memory().unwrap(),
        accounts: AccountStore::in_memory().unwrap(),
        admin_token: admin.then(|| TOKEN.into()),
        allow_registration: false,
        releases: ReleaseStore::new(dir.path(), 120),
    })
}
async fn send(app: &Router, request: Request<Body>) -> Response {
    app.clone().oneshot(request).await.unwrap()
}
async fn bytes(response: Response) -> Vec<u8> {
    to_bytes(response.into_body(), 8192).await.unwrap().to_vec()
}
async fn get(app: &Router, url: &str) -> Response {
    send(app, Request::get(url).body(Body::empty()).unwrap()).await
}
fn hash(data: &[u8]) -> String {
    format!("{:X}", Sha256::digest(data))
}
fn upload(data: &'static [u8], length: usize, checksum: &str) -> Request<Body> {
    Request::put(PACKAGE)
        .header(header::AUTHORIZATION, format!("Bearer {TOKEN}"))
        .header(header::CONTENT_LENGTH, length)
        .header("X-UMD-SHA256", checksum)
        .body(Body::from(data))
        .unwrap()
}
fn publish(manifest: &Value) -> Request<Body> {
    Request::put(LATEST)
        .header(header::AUTHORIZATION, format!("Bearer {TOKEN}"))
        .header(header::CONTENT_TYPE, "application/json")
        .body(Body::from(manifest.to_string()))
        .unwrap()
}
#[tokio::test]
async fn package_upload_is_atomic_and_resumable_across_restart() {
    let dir = TempDir::new().unwrap();
    let server = app(&dir, true);
    let original = b"original package";
    assert_eq!(
        get(&server, NORMALIZED).await.status(),
        StatusCode::NOT_FOUND
    );
    let ok = send(
        &server,
        upload(original, original.len(), &hash(original).to_lowercase()),
    )
    .await;
    assert_eq!(ok.status(), StatusCode::OK);
    assert_eq!(
        serde_json::from_slice::<Value>(&bytes(ok).await).unwrap(),
        json!({"sizeBytes":original.len(),"relativePath":NORMALIZED,"sha256":hash(original)})
    );
    for req in [
        upload(b"wrong", 5, &hash(original)),
        upload(b"x", 3, &hash(b"x")),
        upload(b"abc", 2, &hash(b"abc")),
        upload(b"", 121, &hash(b"")),
    ] {
        assert_eq!(send(&server, req).await.status(), StatusCode::BAD_REQUEST);
    }
    let folder = dir.path().join("packages/stable/win-x64/1.2");
    assert_eq!(std::fs::read_dir(folder).unwrap().count(), 1);
    let readonly = app(&dir, false);
    assert_eq!(bytes(get(&readonly, NORMALIZED).await).await, original);
    let partial = send(
        &readonly,
        Request::get(NORMALIZED)
            .header(header::RANGE, "bytes=1-4")
            .body(Body::empty())
            .unwrap(),
    )
    .await;
    assert_eq!(partial.status(), StatusCode::PARTIAL_CONTENT);
    assert_eq!(partial.headers()[header::CONTENT_RANGE], "bytes 1-4/16");
    assert_eq!(bytes(partial).await, &original[1..5]);
    let head = send(
        &readonly,
        Request::head(NORMALIZED).body(Body::empty()).unwrap(),
    )
    .await;
    assert_eq!(head.headers()[header::CONTENT_LENGTH], "16");
    assert!(bytes(head).await.is_empty());
    let newer = b"replacement";
    assert_eq!(
        send(&server, upload(newer, newer.len(), &hash(newer)))
            .await
            .status(),
        StatusCode::OK
    );
    assert_eq!(bytes(get(&readonly, NORMALIZED).await).await, newer);
}
#[tokio::test]
async fn manifest_matches_client_contract_and_failed_publish_preserves_previous_version() {
    let dir = TempDir::new().unwrap();
    let server = app(&dir, true);
    let manifest = json!({"version":"1.2","channel":"Stable","runtimeIdentifier":"WIN-x64",
       "downloadUri":"https://updates.example/package.zip","sha256":hash(b"test"),"sizeBytes":4,
       "publishedAt":"2026-01-02T12:00:00.1234567+00:00","signingKeyId":"test-key",
       "signatureAlgorithm":"ECDSA-P256-SHA256","signature":STANDARD.encode([0;64]),"releaseNotes":"Novedades"});
    assert_eq!(
        send(&server, publish(&manifest)).await.status(),
        StatusCode::OK
    );
    for (field, value) in [
        ("channel", json!("wrong")),
        ("runtimeIdentifier", json!("linux-arm64")),
        ("signature", json!("AA==")),
        ("signatureAlgorithm", json!("unrecognized")),
        ("downloadUri", json!("file:///package")),
        ("sizeBytes", json!(0)),
        ("sha256", json!("123")),
    ] {
        let mut changed = manifest.clone();
        changed[field] = value;
        assert_eq!(
            send(&server, publish(&changed)).await.status(),
            StatusCode::BAD_REQUEST
        );
    }
    let readonly = app(&dir, false);
    assert_eq!(
        serde_json::from_slice::<Value>(&bytes(get(&readonly, LATEST).await).await).unwrap(),
        manifest
    );
    assert_eq!(
        get(&server, "/api/v1/releases/beta/win-x64/latest")
            .await
            .status(),
        StatusCode::NOT_FOUND
    );
    for (application, enabled) in [(&server, true), (&readonly, false)] {
        let caps: Value =
            serde_json::from_slice(&bytes(get(application, "/api/v1/capabilities").await).await)
                .unwrap();
        assert_eq!(caps["releaseUploads"], enabled);
        assert_eq!(caps["releaseNotifications"], true);
    }
    assert_eq!(
        send(&readonly, publish(&manifest)).await.status(),
        StatusCode::SERVICE_UNAVAILABLE
    );
    for uri in [LATEST, PACKAGE] {
        let denied = send(&server, Request::put(uri).body(Body::empty()).unwrap()).await;
        assert_eq!(denied.status(), StatusCode::UNAUTHORIZED);
    }
}
#[tokio::test]
async fn readers_keep_the_complete_version_while_replacement_is_in_flight() {
    use axum::body::Bytes;
    use futures_util::{stream, StreamExt};
    use std::io;
    use tokio::sync::oneshot;

    let directory = TempDir::new().unwrap();
    let server = app(&directory, true);
    let old = b"old package";
    assert_eq!(
        send(&server, upload(old, old.len(), &hash(old)))
            .await
            .status(),
        StatusCode::OK
    );

    let (started, in_progress) = oneshot::channel();
    let (finish, wait) = oneshot::channel();
    let stream = stream::iter([Ok::<_, io::Error>(Bytes::from_static(b"new "))]).chain(
        stream::once(async {
            started.send(()).unwrap();
            wait.await.map_err(io::Error::other)?;
            Ok(Bytes::from_static(b"package"))
        }),
    );
    let mut replacement = upload(b"", 11, &hash(b"new package"));
    *replacement.body_mut() = Body::from_stream(stream);
    let writer = tokio::spawn(server.clone().oneshot(replacement));
    in_progress.await.unwrap();
    assert_eq!(bytes(get(&server, PACKAGE).await).await, old);
    finish.send(()).unwrap();
    assert_eq!(writer.await.unwrap().unwrap().status(), StatusCode::OK);
    assert_eq!(bytes(get(&server, PACKAGE).await).await, b"new package");
}
