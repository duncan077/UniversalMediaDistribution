use axum::{
    body::{to_bytes, Body},
    http::{Request, StatusCode},
    Router,
};
use serde_json::{json, Value};
use tower::ServiceExt;
use umd_core::{CatalogDocument, CatalogStore};
use umd_server::{router, AccountStore, ServerState};
const ADMIN: &str = "private-administrator-token-for-tests";
fn app(allow_registration: bool) -> Router {
    let directory = std::sync::Arc::new(tempfile::tempdir().unwrap());
    router(ServerState {
        admin: umd_server::Admin::open(directory.path()).unwrap(),
        catalog: CatalogStore::in_memory().unwrap(),
        accounts: AccountStore::in_memory().unwrap(),
        releases: umd_server::ReleaseStore::new(
            std::env::temp_dir().join(uuid::Uuid::new_v4().to_string()),
            umd_server::DEFAULT_MAX_PACKAGE_BYTES,
        ),
        admin_token: Some(ADMIN.into()),
        allow_registration,
    })
    .layer(axum::Extension(directory))
}
async fn request(
    app: &Router,
    method: &str,
    path: &str,
    token: &str,
    body: Value,
) -> (StatusCode, Value) {
    let mut builder = Request::builder()
        .method(method)
        .uri(path)
        .header("content-type", "application/json");
    if !token.is_empty() {
        builder = builder.header("authorization", format!("Bearer {token}"));
    }
    let response = app
        .clone()
        .oneshot(
            builder
                .body(if body.is_null() {
                    Body::empty()
                } else {
                    Body::from(body.to_string())
                })
                .unwrap(),
        )
        .await
        .unwrap();
    let status = response.status();
    let bytes = to_bytes(response.into_body(), umd_core::MAX_DOCUMENT_BYTES)
        .await
        .unwrap();
    (
        status,
        serde_json::from_slice(&bytes).unwrap_or(Value::Null),
    )
}
async fn register(app: &Router, name: &str) -> String {
    let (status,result)=request(app,"POST","/api/v1/accounts/register","",json!({"username":name,"password":"a-test-password-123","displayName":name,"deviceName":"test"})).await;
    assert_eq!(status, StatusCode::CREATED, "{result}");
    let token = result["accessToken"].as_str().unwrap();
    assert_eq!(token.len(), 43);
    assert!(token
        .bytes()
        .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'-' | b'_')));
    token.into()
}
fn backup() -> Value {
    let local = CatalogStore::in_memory().unwrap();
    local.import(&CatalogDocument::new(vec![]), false).unwrap();
    local.backup().unwrap()
}

struct FriendClient<'a> {
    app: &'a Router,
    token: String,
}

#[tokio::test]
async fn incremental_sync_merges_concurrent_changes_and_rejects_stale_baselines() {
    let app = app(true);
    let token = register(&app, "incremental").await;
    let other = register(&app, "independent").await;
    let sync = "/api/v1/sync/snapshot";
    let patch = "/api/v1/sync/snapshot/progress";
    let changes = "/api/v1/sync/changes?since=1";
    let id = uuid::Uuid::new_v4();
    let second = uuid::Uuid::new_v4();
    let local = CatalogStore::in_memory().unwrap();
    let catalog = json!({"format":"universal-media-distribution.catalog","version":1,"items":[
        {"mediaType":"movie","subtype":"movie","name":"Primera","syncId":id},
        {"mediaType":"movie","subtype":"movie","name":"Segunda","syncId":second}]});
    local
        .import(
            &CatalogDocument::parse(&serde_json::to_vec(&catalog).unwrap()).unwrap(),
            false,
        )
        .unwrap();
    let base = local.backup().unwrap();
    let target = umd_core::ProgressTarget {
        item_id: id,
        unit_kind: None,
        display_number: None,
        season_number: None,
    };
    local
        .save_progress(&target, 17.0, "seconds", false)
        .unwrap();
    let early = local.backup().unwrap();
    local
        .save_progress(&target, 42.0, "seconds", false)
        .unwrap();
    let part = umd_core::backup_patch(&base, &local.backup().unwrap()).unwrap();
    assert_eq!(part["catalog"]["items"].as_array().unwrap().len(), 1);
    assert_eq!(
        request(&app, "PATCH", patch, &token, json!({"patch":part}))
            .await
            .0,
        StatusCode::CONFLICT
    );
    assert_eq!(
        request(
            &app,
            "PUT",
            sync,
            &token,
            json!({"expectedRevision":0,"document":base})
        )
        .await
        .0,
        StatusCode::OK
    );
    assert_eq!(
        request(&app, "GET", changes, &other, Value::Null).await.0,
        StatusCode::NOT_FOUND
    );
    assert_eq!(
        request(&app, "GET", changes, "", Value::Null).await.0,
        StatusCode::UNAUTHORIZED
    );
    let device = CatalogStore::in_memory().unwrap();
    device.restore_backup(&base).unwrap();
    device.set_favorite(second, true).unwrap();
    let favorite = umd_core::backup_patch(&base, &device.backup().unwrap()).unwrap();
    let (one, two) = tokio::join!(
        request(&app, "PATCH", patch, &token, json!({"patch":part})),
        request(&app, "PATCH", patch, &token, json!({"patch":favorite}))
    );
    assert_eq!((one.0, two.0), (StatusCode::OK, StatusCode::OK));
    assert_eq!(
        request(
            &app,
            "PATCH",
            patch,
            &token,
            json!({"patch":part,"expectedRevision":1})
        )
        .await
        .0,
        StatusCode::CONFLICT
    );
    let stale = umd_core::backup_patch(&base, &early).unwrap();
    assert_eq!(
        request(&app, "PATCH", patch, &token, json!({"patch":stale}))
            .await
            .0,
        StatusCode::OK
    );
    let (_, delta) = request(&app, "GET", changes, &token, Value::Null).await;
    assert_eq!(delta["fromRevision"], 1);
    assert_eq!(delta["toRevision"], 4);
    assert_eq!(delta["full"], false);
    assert_eq!(delta["document"]["progress"][0]["position"], 42.0);
    let rebuilt = umd_core::apply_received_changes(&base, &delta["document"])
        .unwrap()
        .unwrap();
    assert_eq!(
        rebuilt["favoriteItemStates"][0]["item"]["itemId"],
        second.to_string()
    );
    let (_, idle) = request(
        &app,
        "GET",
        "/api/v1/sync/changes?since=4",
        &token,
        Value::Null,
    )
    .await;
    assert!(umd_core::backup_patch_empty(&idle["document"]));
    assert_eq!(
        request(
            &app,
            "PUT",
            sync,
            &token,
            json!({"expectedRevision":4,"document":base})
        )
        .await
        .0,
        StatusCode::OK
    );
    assert_eq!(
        request(&app, "GET", changes, &token, Value::Null).await.1["full"],
        true
    );
    assert_eq!(
        request(&app, "DELETE", sync, &token, json!({"expectedRevision":5}))
            .await
            .0,
        StatusCode::OK
    );
    assert_eq!(
        request(&app, "GET", changes, &token, Value::Null).await.0,
        StatusCode::NOT_FOUND
    );
    assert_eq!(
        request(&app, "PATCH", patch, &token, json!({"patch":part}))
            .await
            .0,
        StatusCode::CONFLICT
    );
}
impl<'a> FriendClient<'a> {
    async fn new(app: &'a Router, name: &str) -> Self {
        Self {
            app,
            token: register(app, name).await,
        }
    }
    async fn call(&self, verb: &str, path: &str, body: Value, expected: u16) -> Value {
        let (status, value) = request(
            self.app,
            verb,
            &format!("/api/v1/{path}"),
            &self.token,
            body,
        )
        .await;
        assert_eq!(status.as_u16(), expected, "{verb} {path}: {value}");
        value
    }
    async fn settings(&self, sharing: bool, discoverable: bool, hidden: &[&str]) {
        self.call("PUT", "friends/visibility", json!({"shareProgress":sharing,"discoverableByUsername":discoverable,"hiddenItemKeys":hidden}), 200).await;
    }
    async fn lookup(&self, key: &str) -> Vec<Value> {
        self.call(
            "POST",
            "friends/activity/lookup",
            json!({"itemKeys":[key]}),
            200,
        )
        .await["matches"]
            .as_array()
            .unwrap()
            .clone()
    }
}

#[tokio::test]
async fn friend_requests_and_progress_follow_both_accounts_privacy_choices() {
    let app = app(true);
    let alice = FriendClient::new(&app, "friendalice").await;
    let bob = FriendClient::new(&app, "friendbob").await;
    let stranger = FriendClient::new(&app, "thirdparty").await;
    let none = Value::Null;
    let requests = "friends/requests";
    let send_bob = json!({"username":"FRIENDBOB"});
    let hidden = "movie:TÍTULO";
    assert_eq!(
        umd_core::friend_item_key("Movie", " ti\u{301}tulo "),
        hidden
    );
    assert_eq!(
        request(&app, "GET", "/api/v1/friends", "", none.clone())
            .await
            .0,
        StatusCode::UNAUTHORIZED
    );
    alice
        .call("POST", requests, json!({"username":"friendalice"}), 409)
        .await;
    bob.settings(false, false, &[]).await;
    assert_eq!(
        alice.call("POST", requests, send_bob.clone(), 404).await,
        alice
            .call("POST", requests, json!({"username":"nobody"}), 404)
            .await
    );
    bob.settings(false, true, &[]).await;
    let outgoing = alice.call("POST", requests, send_bob.clone(), 200).await;
    assert_eq!(
        outgoing,
        alice.call("POST", requests, send_bob.clone(), 200).await
    );
    let b = outgoing["accountId"].as_str().unwrap();
    let incoming = bob.call("GET", requests, none.clone(), 200).await;
    assert_eq!(incoming[0]["direction"], "incoming");
    let a = incoming[0]["accountId"].as_str().unwrap();
    stranger
        .call("POST", &format!("{requests}/{a}/accept"), none.clone(), 404)
        .await;
    alice
        .call("POST", &format!("{requests}/{b}/accept"), none.clone(), 404)
        .await;
    bob.call("DELETE", &format!("{requests}/{a}"), none.clone(), 204)
        .await;
    assert_eq!(
        alice.call("GET", requests, none.clone(), 200).await,
        json!([])
    );
    alice.call("POST", requests, send_bob.clone(), 200).await;
    assert_eq!(
        bob.call("POST", requests, json!({"username":"friendalice"}), 200)
            .await["direction"],
        "accepted"
    );
    alice.call("POST", requests, send_bob.clone(), 409).await;

    let library = CatalogStore::in_memory().unwrap();
    let id = uuid::Uuid::new_v4();
    let catalog = json!({"format":"universal-media-distribution.catalog","version":1,"items":[
        {"mediaType":"movie","subtype":"movie","name":"Título","syncId":id}]});
    library
        .import(
            &CatalogDocument::parse(&serde_json::to_vec(&catalog).unwrap()).unwrap(),
            false,
        )
        .unwrap();
    library.set_favorite(id, true).unwrap();
    let sync = "sync/snapshot";
    alice
        .call(
            "PUT",
            sync,
            json!({"expectedRevision":0,"document":library.backup().unwrap()}),
            200,
        )
        .await;
    bob.call(
        "PUT",
        sync,
        json!({"expectedRevision":0,"document":library.backup().unwrap()}),
        200,
    )
    .await;
    bob.settings(true, true, &[]).await;
    assert!(
        alice.lookup(hidden).await.is_empty(),
        "favorite alone is private"
    );
    library
        .save_progress(
            &umd_core::ProgressTarget {
                item_id: id,
                unit_kind: None,
                display_number: None,
                season_number: None,
            },
            63.,
            "seconds",
            true,
        )
        .unwrap();
    bob.call(
        "PUT",
        sync,
        json!({"expectedRevision":1,"document":library.backup().unwrap()}),
        200,
    )
    .await;
    let matches = alice.lookup(hidden).await;
    assert_eq!(matches.len(), 1);
    assert_eq!(matches[0]["isCompleted"], true);
    assert!(matches[0].get("position").is_none());
    assert!(stranger.lookup(hidden).await.is_empty());
    let compare = format!("friends/{b}/comparison");
    let result = alice.call("GET", &compare, none.clone(), 200).await;
    assert_eq!(result["friendOnly"][0]["friendFavorite"], true);
    assert_eq!(result["friendOnly"][0]["viewerFavorite"], true);
    alice
        .call(
            "PUT",
            sync,
            json!({"expectedRevision":1,"document":library.backup().unwrap()}),
            200,
        )
        .await;
    let shared = format!("friends/{b}/shared");
    assert_eq!(
        alice.call("GET", &shared, none.clone(), 200).await["titles"][0]["title"],
        "Título"
    );
    assert_eq!(
        alice.call("GET", &compare, none.clone(), 200).await["inCommon"][0]["title"],
        "Título"
    );
    bob.settings(true, true, &[hidden]).await;
    assert!(alice.lookup(hidden).await.is_empty());
    assert_eq!(
        alice.call("GET", &shared, none.clone(), 200).await["titles"],
        json!([])
    );
    assert_eq!(
        alice.call("GET", &compare, none.clone(), 200).await["viewerOnly"][0]["isCompleted"],
        true
    );
    bob.settings(true, true, &[]).await;
    let directed = format!("friends/{a}/sharing");
    bob.call("PUT", &directed, json!({"shareProgress":false}), 204)
        .await;
    assert!(alice.lookup(hidden).await.is_empty());
    assert!(
        alice.call("GET", "friends", none.clone(), 200).await[0]["progressUpdatedAt"].is_null()
    );
    bob.call("PUT", &directed, json!({"shareProgress":true}), 204)
        .await;
    assert_eq!(alice.lookup(hidden).await.len(), 1);
    bob.settings(false, true, &[]).await;
    assert!(alice.lookup(hidden).await.is_empty());
    bob.settings(true, true, &[]).await;
    bob.call("DELETE", sync, json!({"expectedRevision":2}), 200)
        .await;
    assert!(alice.lookup(hidden).await.is_empty());
    bob.call(
        "PUT",
        sync,
        json!({"expectedRevision":3,"document":library.backup().unwrap()}),
        200,
    )
    .await;
    alice
        .call("DELETE", &format!("friends/{b}"), none.clone(), 204)
        .await;
    assert!(alice.lookup(hidden).await.is_empty());
    assert_eq!(
        bob.call("GET", "friends", none.clone(), 200).await,
        json!([])
    );
    alice.call("POST", requests, send_bob, 200).await;
    bob.call("POST", &format!("{requests}/{a}/accept"), none, 204)
        .await;
    assert_eq!(alice.lookup(hidden).await.len(), 1);
}

#[tokio::test]
async fn published_list_tracks_its_snapshot_and_only_exposes_selected_ids() {
    use umd_core::ListEdit;
    use uuid::Uuid;
    let app = app(true);
    let alice = register(&app, "publisher").await;
    let bob = register(&app, "reader").await;
    let member = Uuid::new_v4();
    let other = Uuid::new_v4();
    let local = CatalogStore::in_memory().unwrap();
    let catalog = json!({"format":"universal-media-distribution.catalog","version":1,"items":[
        {"mediaType":"movie","subtype":"movie","name":"A","syncId":member,"description":"selected"},
        {"mediaType":"movie","subtype":"movie","name":"A","syncId":other,"description":"private"}]});
    local
        .import(
            &CatalogDocument::parse(&serde_json::to_vec(&catalog).unwrap()).unwrap(),
            false,
        )
        .unwrap();
    local
        .edit_list(ListEdit::Create {
            name: "Estrenos".into(),
        })
        .unwrap();
    let id = local.personal_lists().unwrap()[0]["id"]
        .as_str()
        .unwrap()
        .parse()
        .unwrap();
    local
        .edit_list(ListEdit::Member {
            id,
            item_id: member,
            enabled: true,
        })
        .unwrap();
    let shares = "/api/v1/shared-lists";
    let sync = "/api/v1/sync/snapshot";
    let publish = json!({"listId":id});
    assert_eq!(
        request(&app, "POST", shares, "", publish.clone()).await.0,
        StatusCode::UNAUTHORIZED
    );
    assert_eq!(
        request(&app, "POST", shares, &alice, publish.clone())
            .await
            .0,
        StatusCode::CONFLICT
    );
    let upload = |rev| json!({"document":local.backup().unwrap(),"expectedRevision":rev});
    assert_eq!(
        request(&app, "PUT", sync, &alice, upload(0)).await.0,
        StatusCode::OK
    );
    let (status, meta) = request(&app, "POST", shares, &alice, publish.clone()).await;
    assert_eq!(status, StatusCode::OK, "{meta}");
    let path = meta["catalogPath"].as_str().unwrap();
    let info = path.strip_suffix("/catalog").unwrap();
    assert_eq!(
        request(&app, "POST", shares, &alice, publish.clone())
            .await
            .1,
        meta
    );
    assert_eq!(
        request(&app, "GET", shares, &bob, Value::Null).await.1,
        json!([])
    );
    assert_eq!(request(&app, "GET", info, "", Value::Null).await.1, meta);
    let doc = request(&app, "GET", path, "", Value::Null).await.1;
    assert_eq!(doc["items"].as_array().unwrap().len(), 1);
    assert_eq!(doc["items"][0]["syncId"], member.to_string());
    assert!(doc.get("progress").is_none());
    assert_eq!(
        request(&app, "DELETE", info, &bob, Value::Null).await.0,
        StatusCode::FORBIDDEN
    );
    local
        .edit_list(ListEdit::Rename {
            id,
            name: "Vista".into(),
        })
        .unwrap();
    local
        .edit_list(ListEdit::Member {
            id,
            item_id: member,
            enabled: false,
        })
        .unwrap();
    assert_eq!(
        request(&app, "PUT", sync, &alice, upload(1)).await.0,
        StatusCode::OK
    );
    assert_eq!(
        request(&app, "GET", info, "", Value::Null).await.1["name"],
        "Vista"
    );
    assert_eq!(
        request(&app, "GET", path, "", Value::Null).await.1["items"],
        json!([])
    );
    assert_eq!(
        request(&app, "DELETE", info, &alice, Value::Null).await.0,
        StatusCode::NO_CONTENT
    );
    let (_, new) = request(&app, "POST", shares, &alice, publish).await;
    let new_path = new["catalogPath"].as_str().unwrap();
    assert_ne!(new_path, path);
    assert_eq!(
        request(&app, "GET", path, "", Value::Null).await.0,
        StatusCode::NOT_FOUND
    );
    local.edit_list(ListEdit::Delete { id }).unwrap();
    assert_eq!(
        request(&app, "PUT", sync, &alice, upload(2)).await.0,
        StatusCode::OK
    );
    assert_eq!(
        request(&app, "GET", new_path, "", Value::Null).await.0,
        StatusCode::NOT_FOUND
    );
}
#[tokio::test]
async fn stale_v4_put_preserves_edits_from_both_devices() {
    let app = app(true);
    let token = register(&app, "converge").await;
    let path = "/api/v1/sync/snapshot";
    let mut ids = Vec::new();
    for title in ["Película A", "Película B"] {
        let device = CatalogStore::in_memory().unwrap();
        let id = uuid::Uuid::new_v4();
        ids.push(id);
        let item = serde_json::from_value(
            json!({"name":title,"mediaType":"movie","subtype":"movie","syncId":id}),
        )
        .unwrap();
        device
            .import(&CatalogDocument::new(vec![item]), false)
            .unwrap();
        device.set_favorite(id, true).unwrap();
        let payload = json!({"expectedRevision":0,"document":device.backup().unwrap()});
        assert_eq!(
            request(&app, "PUT", path, &token, payload).await.0,
            StatusCode::OK
        );
    }
    let (_, latest) = request(&app, "GET", path, &token, Value::Null).await;
    assert_eq!(latest["revision"], 2);
    let doc = &latest["document"];
    for id in ids {
        assert!(doc["catalog"]["items"]
            .as_array()
            .unwrap()
            .iter()
            .any(|item| item["syncId"] == id.to_string()));
        assert!(doc["favoriteItemStates"]
            .as_array()
            .unwrap()
            .iter()
            .any(|item| item["item"]["itemId"] == id.to_string()));
    }
}

#[tokio::test]
async fn conditional_snapshot_reads_are_private_and_reflect_new_revisions() {
    let app = app(true);
    let first = register(&app, "condfirst").await;
    let second = register(&app, "condsecond").await;
    let get = |token: &str, tag: &str| {
        app.clone().oneshot(
            Request::builder()
                .uri("/api/v1/sync/snapshot")
                .header("authorization", format!("Bearer {token}"))
                .header("if-none-match", tag)
                .body(Body::empty())
                .unwrap(),
        )
    };
    let initial = get(&first, "").await.unwrap();
    let tag = initial.headers()["etag"].to_str().unwrap();
    let cached = get(&first, tag).await.unwrap();
    assert_eq!(cached.status(), StatusCode::NOT_MODIFIED);
    assert!(to_bytes(cached.into_body(), 1024).await.unwrap().is_empty());
    assert_eq!(get(&second, tag).await.unwrap().status(), StatusCode::OK);
    assert_eq!(
        get("invalid", tag).await.unwrap().status(),
        StatusCode::UNAUTHORIZED
    );
    let result = request(
        &app,
        "PUT",
        "/api/v1/sync/snapshot",
        &first,
        json!({"expectedRevision":0,"document":backup()}),
    )
    .await;
    assert_eq!(result.0, StatusCode::OK);
    let changed = get(&first, tag).await.unwrap();
    assert_eq!(changed.status(), StatusCode::OK);
    assert_ne!(changed.headers()["etag"], tag);
}

#[tokio::test]
async fn compressed_snapshot_can_be_restored() {
    use std::io::Write;
    let app = app(true);
    let token = register(&app, "compressed").await;
    let document = backup();
    let mut gzip = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::fast());
    gzip.write_all(
        &serde_json::to_vec(&json!({"expectedRevision":0, "document":document})).unwrap(),
    )
    .unwrap();
    let request_body = Request::put("/api/v1/sync/snapshot")
        .header("authorization", format!("Bearer {token}"))
        .header("content-type", "application/json")
        .header("content-encoding", "gzip")
        .body(Body::from(gzip.finish().unwrap()))
        .unwrap();
    assert_eq!(
        app.clone().oneshot(request_body).await.unwrap().status(),
        StatusCode::OK
    );
    let (_, saved) = request(&app, "GET", "/api/v1/sync/snapshot", &token, Value::Null).await;
    assert_eq!(saved["document"], document);
}
#[tokio::test]
async fn account_snapshots_are_private_and_revisions_prevent_lost_updates() {
    let app = app(true);
    let alice = register(&app, "alice").await;
    let bob = register(&app, "bob").await;
    assert_eq!(
        request(&app, "GET", "/api/v1/sync/snapshot", "", Value::Null)
            .await
            .0,
        StatusCode::UNAUTHORIZED
    );
    let upload = json!({"expectedRevision":0,"document":backup()});
    let (status, saved) =
        request(&app, "PUT", "/api/v1/sync/snapshot", &alice, upload.clone()).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(saved["revision"], 1);
    let (_, private) = request(&app, "GET", "/api/v1/sync/snapshot", &bob, Value::Null).await;
    assert_eq!(private["revision"], 0);
    assert!(private["document"].is_null());
    let mut legacy = upload.clone();
    legacy["document"]["version"] = json!(3);
    let (status, conflict) = request(&app, "PUT", "/api/v1/sync/snapshot", &alice, legacy).await;
    assert_eq!(status, StatusCode::CONFLICT);
    assert_eq!(conflict["currentRevision"], 1);
    let (status, merged) = request(&app, "PUT", "/api/v1/sync/snapshot", &alice, upload).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(merged["revision"], 2);
    let (status, _) = request(
        &app,
        "DELETE",
        "/api/v1/sync/snapshot",
        &alice,
        json!({"expectedRevision":2}),
    )
    .await;
    assert_eq!(status, StatusCode::OK);
    let (_, deleted) = request(&app, "GET", "/api/v1/sync/snapshot", &alice, Value::Null).await;
    assert_eq!(deleted["revision"], 3);
    assert!(deleted["document"].is_null());
}
#[tokio::test]
async fn registration_admin_login_and_session_revocation_work() {
    let app = app(false);
    let credentials = json!({"username":"Test.User","password":"a-test-password-123","displayName":"Test User","deviceName":"test"});
    assert_eq!(
        request(
            &app,
            "POST",
            "/api/v1/accounts/register",
            "",
            credentials.clone()
        )
        .await
        .0,
        StatusCode::FORBIDDEN
    );
    assert_eq!(
        request(
            &app,
            "POST",
            "/api/v1/admin/accounts",
            "wrong",
            credentials.clone()
        )
        .await
        .0,
        StatusCode::UNAUTHORIZED
    );
    assert_eq!(
        request(
            &app,
            "POST",
            "/api/v1/admin/accounts",
            ADMIN,
            credentials.clone()
        )
        .await
        .0,
        StatusCode::CREATED
    );
    assert_eq!(
        request(
            &app,
            "POST",
            "/api/v1/admin/accounts",
            ADMIN,
            credentials.clone()
        )
        .await
        .0,
        StatusCode::CONFLICT
    );
    let mut wrong = credentials.clone();
    wrong["password"] = json!("another-wrong-password");
    assert_eq!(
        request(&app, "POST", "/api/v1/accounts/login", "", wrong)
            .await
            .0,
        StatusCode::UNAUTHORIZED
    );
    let (status, session) = request(&app, "POST", "/api/v1/accounts/login", "", credentials).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(session["username"], "test.user");
    let token = session["accessToken"].as_str().unwrap();
    assert_eq!(
        request(&app, "GET", "/api/v1/accounts/me", token, Value::Null)
            .await
            .0,
        StatusCode::OK
    );
    assert_eq!(
        request(
            &app,
            "DELETE",
            "/api/v1/accounts/session",
            token,
            Value::Null
        )
        .await
        .0,
        StatusCode::NO_CONTENT
    );
    assert_eq!(
        request(&app, "GET", "/api/v1/accounts/me", token, Value::Null)
            .await
            .0,
        StatusCode::UNAUTHORIZED
    );
}
#[tokio::test]
async fn sync_bodies_are_not_read_without_a_session() {
    let app = app(true);
    // An unreadable body would be a 4xx parse error if it were read first.
    for (method, path) in [
        ("PUT", "/api/v1/sync/snapshot"),
        ("PATCH", "/api/v1/sync/snapshot/progress"),
    ] {
        for token in [None, Some("Bearer not-a-session")] {
            let mut request = Request::builder()
                .method(method)
                .uri(path)
                .header("content-type", "application/json");
            if let Some(token) = token {
                request = request.header("authorization", token);
            }
            let response = app
                .clone()
                .oneshot(request.body(Body::from("{ not json")).unwrap())
                .await
                .unwrap();
            assert_eq!(
                response.status(),
                StatusCode::UNAUTHORIZED,
                "{method} {path}"
            );
        }
    }
    let unread = app
        .oneshot(
            Request::get("/api/v1/sync/changes?since=0")
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(unread.status(), StatusCode::UNAUTHORIZED);
}

#[tokio::test]
async fn invalid_snapshot_does_not_replace_existing_backup() {
    let app = app(true);
    let token = register(&app, "reader").await;
    let doc = backup();
    assert_eq!(
        request(
            &app,
            "PUT",
            "/api/v1/sync/snapshot",
            &token,
            json!({"expectedRevision":0,"document":doc})
        )
        .await
        .0,
        StatusCode::OK
    );
    assert_eq!(
        request(
            &app,
            "PUT",
            "/api/v1/sync/snapshot",
            &token,
            json!({"expectedRevision":1,"document":{"format":"wrong"}})
        )
        .await
        .0,
        StatusCode::BAD_REQUEST
    );
    let (_, saved) = request(&app, "GET", "/api/v1/sync/snapshot", &token, Value::Null).await;
    assert_eq!(saved["revision"], 1);
}
