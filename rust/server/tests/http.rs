use axum::{
    body::{to_bytes, Body},
    http::{Request, StatusCode},
};
use serde_json::{json, Value};
use tower::ServiceExt;
use umd_core::CatalogStore;
use umd_server::{router, ServerState};

const TOKEN: &str = "test-admin-token-with-at-least-32-bytes";

#[tokio::test]
async fn filtered_summary_preserves_id_and_full_detail() {
    let app = app();
    let document = json!({"format":"universal-media-distribution.catalog","version":1,"items":[
        {"name":"Serie","mediaType":"movie","subtype":"movie.anime_series","genres":["Acción"],"categories":["Juvenil"],
         "releaseDate":"2020-11-11","publicationStatus":"Finished Airing","units":[{"kind":"episode","displayNumber":"1"}],
         "contributors":[{"name":"Distribución","credits":[{"role":"distributor"}]}]},
        {"name":"Otra","mediaType":"movie","subtype":"movie"}]});
    let uploaded = app
        .clone()
        .oneshot(
            Request::put("/api/v1/catalog/current")
                .header("authorization", format!("Bearer {TOKEN}"))
                .body(Body::from(document.to_string()))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(uploaded.status(), StatusCode::OK);
    let response = app.clone().oneshot(Request::get("/api/v1/catalog/search?genre=action&year=2020&category=juvenil&status=completed&subtypeGroup=anime&provider=Distribuci%C3%B3n&summary=true&limit=500")
        .body(Body::empty()).unwrap()).await.unwrap();
    assert_eq!(response.headers()["x-umd-summary"], "true");
    let body: Value =
        serde_json::from_slice(&to_bytes(response.into_body(), 10000).await.unwrap()).unwrap();
    assert_eq!(body["items"].as_array().unwrap().len(), 1);
    let row = &body["items"][0];
    assert_eq!(row["name"], "Serie");
    assert!(row.get("units").is_none());
    let detail = app
        .oneshot(
            Request::get(format!(
                "/api/v1/catalog/items/{}",
                row["syncId"].as_str().unwrap()
            ))
            .body(Body::empty())
            .unwrap(),
        )
        .await
        .unwrap();
    let detail: Value =
        serde_json::from_slice(&to_bytes(detail.into_body(), 10000).await.unwrap()).unwrap();
    assert_eq!(detail["items"][0]["units"][0]["displayNumber"], "1");
}

fn app() -> axum::Router {
    let directory = std::sync::Arc::new(tempfile::tempdir().unwrap());
    router(ServerState {
        admin: umd_server::Admin::open(directory.path()).unwrap(),
        catalog: CatalogStore::in_memory().unwrap(),
        releases: umd_server::ReleaseStore::new(
            std::env::temp_dir().join(uuid::Uuid::new_v4().to_string()),
            umd_server::DEFAULT_MAX_PACKAGE_BYTES,
        ),
        admin_token: Some(TOKEN.into()),
        accounts: umd_server::AccountStore::in_memory().unwrap(),
        allow_registration: false,
    })
    .layer(axum::Extension(directory))
}

fn payload(name: &str) -> String {
    json!({
        "format":"universal-media-distribution.catalog", "version":1,
        "items":[{"name":name,"mediaType":"movie","subtype":"movie","sources":[]}]
    })
    .to_string()
}

async fn publish(app: axum::Router, name: &str) -> axum::response::Response {
    app.oneshot(
        Request::put("/api/v1/catalog/current")
            .header("authorization", format!("Bearer {TOKEN}"))
            .header("content-type", "application/json")
            .body(Body::from(payload(name)))
            .unwrap(),
    )
    .await
    .unwrap()
}

#[tokio::test]
async fn public_catalog_roundtrip_matches_dotnet_document_and_page_contracts() {
    let app = app();
    assert_eq!(
        publish(app.clone(), "Open skies").await.status(),
        StatusCode::OK
    );
    let response = app
        .clone()
        .oneshot(
            Request::get("/api/v1/catalog/items?limit=1")
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    let body: Value =
        serde_json::from_slice(&to_bytes(response.into_body(), 10000).await.unwrap()).unwrap();
    assert_eq!(body["revision"], 1);
    assert_eq!(body["document"]["items"][0]["name"], "Open skies");
    let id = body["document"]["items"][0]["syncId"].as_str().unwrap();
    let response = app
        .oneshot(
            Request::get(format!("/api/v1/catalog/items/{id}"))
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::OK);
}

#[tokio::test]
async fn unauthorized_publish_and_invalid_replace_leave_catalog_intact() {
    let app = app();
    assert_eq!(
        publish(app.clone(), "Keep me").await.status(),
        StatusCode::OK
    );
    let response = app
        .clone()
        .oneshot(
            Request::put("/api/v1/catalog/current")
                .header("content-type", "application/json")
                .body(Body::from(payload("Denied")))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
    let response = app
        .clone()
        .oneshot(
            Request::put("/api/v1/catalog/current")
                .header("authorization", format!("Bearer {TOKEN}"))
                .body(Body::from("{}"))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::BAD_REQUEST);
    let response = app
        .oneshot(
            Request::get("/api/v1/catalog/current")
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    let body: Value =
        serde_json::from_slice(&to_bytes(response.into_body(), 10000).await.unwrap()).unwrap();
    assert_eq!(body["items"][0]["name"], "Keep me");
}

#[tokio::test]
async fn advertises_only_implemented_capabilities_and_rejects_unknown_search_filters() {
    let app = app();
    let response = app
        .clone()
        .oneshot(
            Request::get("/api/v1/capabilities")
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    let body: Value =
        serde_json::from_slice(&to_bytes(response.into_body(), 10000).await.unwrap()).unwrap();
    assert_eq!(body["onlineCatalog"], true);
    assert_eq!(body["accounts"], true);
    let response = app
        .oneshot(
            Request::get("/api/v1/catalog/search?unrecognized=action")
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::BAD_REQUEST);
}

#[tokio::test]
async fn catalog_is_gzipped_when_the_client_accepts_it() {
    let app = app();
    let uploaded = app
        .clone()
        .oneshot(
            Request::put("/api/v1/catalog/current")
                .header("authorization", format!("Bearer {TOKEN}"))
                .body(Body::from(payload("Comprimida")))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(uploaded.status(), StatusCode::OK);
    let get = |path: &str, gzip: bool| {
        let mut request = Request::get(path);
        if gzip {
            request = request.header("accept-encoding", "gzip");
        }
        app.clone().oneshot(request.body(Body::empty()).unwrap())
    };
    let plain = get("/api/v1/catalog/current", false).await.unwrap();
    assert!(plain.headers().get("content-encoding").is_none());
    let plain = to_bytes(plain.into_body(), 100_000).await.unwrap();

    let packed = get("/api/v1/catalog/current", true).await.unwrap();
    assert_eq!(packed.headers()["content-encoding"], "gzip");
    let packed = to_bytes(packed.into_body(), 100_000).await.unwrap();
    let mut text = Vec::new();
    std::io::Read::read_to_end(&mut flate2::read::GzDecoder::new(&packed[..]), &mut text).unwrap();
    assert_eq!(text, plain);

    // Routes outside the catalog and sync documents are left as they are.
    let health = get("/health", true).await.unwrap();
    assert!(health.headers().get("content-encoding").is_none());
}
