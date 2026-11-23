use super::*;
use crate::{AccountStore, ReleaseStore};
use axum::{
    body::{to_bytes, Body},
    http::Request,
    Router,
};
use std::{net::SocketAddr, sync::Arc};
use tempfile::TempDir;
use tower::ServiceExt;
use umd_core::{CatalogDocument, CatalogStore};
const TOKEN: &str = "administrative-test-token-32-bytes";
const EXTERNAL: &str = "umd-json-api";

async fn fixture(router: Router) -> SocketAddr {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, router).await.unwrap() });
    address
}
fn setup(dir: &TempDir, addr: SocketAddr, id: &str) -> ServerState {
    let mut cfg = source::Config::read().unwrap();
    cfg.disabled = PROVIDERS
        .iter()
        .filter(|(p, _)| *p != id)
        .map(|(p, _)| p.to_string())
        .collect();
    cfg.bases.insert(
        id.into(),
        format!("http://fixture.example:{}/", addr.port())
            .parse()
            .unwrap(),
    );
    cfg.fixture_address = Some(addr);
    cfg.pages = 1;
    ServerState {
        catalog: CatalogStore::open(dir.path().join("catalog.db")).unwrap(),
        admin: Admin::configured(dir.path(), cfg).unwrap(),
        accounts: AccountStore::in_memory().unwrap(),
        releases: ReleaseStore::new(dir.path(), 1024),
        admin_token: Some(TOKEN.into()),
        allow_registration: false,
    }
}
async fn request(
    app: &Router,
    path: &str,
    token: bool,
    method: &str,
    body: Value,
) -> (StatusCode, Value) {
    let mut req = Request::builder()
        .uri(format!("/api/v1/admin/diagnostics{path}"))
        .method(method)
        .header("content-type", "application/json");
    if token {
        req = req.header("authorization", format!("Bearer {TOKEN}"))
    }
    let res = app
        .clone()
        .oneshot(req.body(Body::from(body.to_string())).unwrap())
        .await
        .unwrap();
    let status = res.status();
    assert_eq!(res.headers()["cache-control"], "no-store");
    (
        status,
        serde_json::from_slice(&to_bytes(res.into_body(), 4_000_000).await.unwrap()).unwrap(),
    )
}
#[tokio::test]
async fn panel_runs_checkpointed_imports_and_preserves_manual_sources() {
    let items=(1..=501).map(|n|serde_json::from_value(json!({"syncId":uuid::Uuid::from_u128(n),"mediaType":"movie","subtype":"movie","name":format!("Título {n}"),"description":"Ficha nueva","thumbnailRef":"no-importar","units":[{"kind":"episode","displayNumber":"1","sources":[{"uri":"proveedor"}]}]})).unwrap()).collect();
    let doc = Arc::new(CatalogDocument::new(items));
    let addr = fixture(Router::new().route(
        "/current",
        get(move || {
            let doc = doc.clone();
            async move {
                tokio::time::sleep(std::time::Duration::from_millis(80)).await;
                Json((*doc).clone())
            }
        }),
    ))
    .await;
    let dir = tempfile::tempdir().unwrap();
    let state = setup(&dir, addr, EXTERNAL);
    let manual=serde_json::from_value(json!({"syncId":uuid::Uuid::from_u128(1),"mediaType":"movie","subtype":"movie","name":"Título 1","sources":[{"uri":"manual"}],"units":[{"kind":"episode","displayNumber":"1","sources":[{"uri":"manual-episodio"}]}]})).unwrap();
    state
        .catalog
        .import(&CatalogDocument::new(vec![manual]), false)
        .unwrap();
    let app = crate::router(state.clone());
    let page = app
        .clone()
        .oneshot(
            Request::builder()
                .uri("/admin")
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(page.status(), 200);
    assert!(page.headers().contains_key("content-security-policy"));
    assert_eq!(request(&app, "", false, "GET", Value::Null).await.0, 401);
    assert_eq!(
        request(
            &app,
            "/provider-interval",
            true,
            "PUT",
            json!({"providerId":EXTERNAL,"intervalSeconds":0})
        )
        .await
        .0,
        400
    );
    assert_eq!(
        request(
            &app,
            "/provider-interval",
            true,
            "PUT",
            json!({"providerId":EXTERNAL,"intervalSeconds":7})
        )
        .await
        .0,
        200
    );
    let (status, reply) =
        request(&app, "/crawl", true, "POST", json!({"providerId":EXTERNAL})).await;
    assert_eq!(status, 202);
    assert!(reply["runId"].is_string());
    assert_eq!(
        request(&app, "/crawl", true, "POST", json!({})).await.0,
        409
    );
    let until = tokio::time::Instant::now() + std::time::Duration::from_secs(30);
    loop {
        let done = state
            .admin
            .db()
            .unwrap()
            .query_row(
                "SELECT finished IS NOT NULL FROM runs WHERE id=?",
                [reply["runId"].as_str().unwrap()],
                |r| r.get::<_, bool>(0),
            )
            .unwrap();
        if done {
            break;
        }
        assert!(tokio::time::Instant::now() < until);
        tokio::time::sleep(std::time::Duration::from_millis(20)).await;
    }
    assert_eq!(state.catalog.crawl_summary().unwrap().0, 500);
    drop(app);
    drop(state);
    let restart = setup(&dir, addr, EXTERNAL);
    assert_eq!(
        restart
            .admin
            .scrape_once(restart.catalog.clone())
            .await
            .unwrap(),
        0
    );
    let summary = restart.catalog.crawl_summary().unwrap();
    assert_eq!(summary.0, 501);
    let item = serde_json::to_value(
        restart
            .catalog
            .item(uuid::Uuid::from_u128(1))
            .unwrap()
            .unwrap()
            .items
            .remove(0),
    )
    .unwrap();
    assert_eq!(item["description"], "Ficha nueva");
    assert!(item["thumbnailRef"].is_null());
    assert_eq!(item["sources"], json!([{"uri":"manual"}]));
    assert_eq!(
        item["units"][0]["sources"],
        json!([{"uri":"manual-episodio"}])
    );
    assert_eq!(
        restart
            .admin
            .scrape_once(restart.catalog.clone())
            .await
            .unwrap(),
        0
    );
    assert_eq!(restart.catalog.crawl_summary().unwrap(), summary);
    let app = crate::router(restart);
    let (status, data) = request(&app, "", true, "GET", Value::Null).await;
    assert_eq!(status, 200);
    assert_eq!(data["catalog"]["titles"], 501);
    assert_eq!(data["crawlerRunning"], false);
    let provider = data["providers"]
        .as_array()
        .unwrap()
        .iter()
        .find(|p| p["id"] == EXTERNAL)
        .unwrap();
    assert_eq!(provider["intervalSeconds"], 7);
    assert_eq!(provider["loaded"], 501);
    assert_eq!(provider["backfillComplete"], true);
    let (_, first) = request(&app, "/logs?limit=1", true, "GET", Value::Null).await;
    let cursor = first["before"].as_i64().unwrap();
    let (_, second) = request(
        &app,
        &format!("/logs?limit=1&before={cursor}"),
        true,
        "GET",
        Value::Null,
    )
    .await;
    assert!(second["items"][0]["id"].as_i64().unwrap() < cursor);
}
#[tokio::test]
async fn manual_retry_respects_throttling_after_restart() {
    let addr = fixture(Router::new().route(
        "/current",
        get(|| async { (StatusCode::TOO_MANY_REQUESTS, [("retry-after", "7200")]) }),
    ))
    .await;
    let dir = tempfile::tempdir().unwrap();
    let s = setup(&dir, addr, EXTERNAL);
    assert_eq!(s.admin.scrape_once(s.catalog.clone()).await.unwrap(), 2);
    drop(s);
    let s = setup(&dir, addr, EXTERNAL);
    let (lock, run) = s.admin.begin().unwrap().unwrap();
    assert_eq!(
        s.admin
            .execute(s.catalog, &run, lock, Some(EXTERNAL.into()), true)
            .await
            .unwrap(),
        "completed"
    );
    let (kind, retry): (String, i64) = s
        .admin
        .db()
        .unwrap()
        .query_row(
            "SELECT kind,retry FROM providers WHERE id=?",
            [EXTERNAL],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .unwrap();
    assert_eq!(kind, "throttled");
    assert!(retry >= now() + 7190);
}
#[tokio::test]
async fn capibara_loads_only_public_chapter_metadata() {
    let ed = json!({"id":42,"title":"Historieta","manga":{"slug":"historieta"},"organization":{"slug":"grupo"},"chapters":[{"number":1},{"number":2,"hasAccess":false}]});
    let detail = ed.clone();
    let addr = fixture(
        Router::new()
            .route(
                "/api/manga-custom",
                get(move || {
                    let ed = ed.clone();
                    async move { Json(json!({"status":true,"data":{"maxPage":1,"items":[ed]}})) }
                }),
            )
            .route(
                "/api/manga-custom/historieta",
                get(move |headers: axum::http::HeaderMap| {
                    let detail = detail.clone();
                    async move {
                        assert_eq!(headers["x-organization"], "grupo");
                        Json(json!({"status":true,"data":detail}))
                    }
                }),
            ),
    )
    .await;
    let dir = tempfile::tempdir().unwrap();
    let s = setup(&dir, addr, "capibaratraductor");
    assert_eq!(s.admin.scrape_once(s.catalog.clone()).await.unwrap(), 0);
    let out = s.catalog.export().unwrap();
    assert_eq!(out.items.len(), 1);
    assert_eq!(out.items[0].metadata["units"].as_array().unwrap().len(), 1);
}
