use axum::{
    extract::Query,
    http::StatusCode,
    routing::{get, post},
    Form, Json, Router,
};
use reqwest::{Client, Url};
use serde_json::{json, Value};
use std::{collections::HashMap, time::Duration};
use umd_providers::{Provider, Site};
async fn source(site: &str, router: Router) -> Provider {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, router).await.unwrap() });
    let base = Url::parse(&format!("http://fixture.example:{}/", addr.port())).unwrap();
    let client = Client::builder()
        .cookie_store(true)
        .no_proxy()
        .redirect(reqwest::redirect::Policy::none())
        .resolve("fixture.example", addr)
        .build()
        .unwrap();
    Provider::new(Site::parse(site).unwrap(), base, client, Duration::ZERO)
}

#[tokio::test]
async fn directory_keeps_legacy_cursor_and_recovers_session_for_all_episodes() {
    let router=Router::new()
        .route("/directorio",get(||async{r#"<script>var animes = {"data":[{"slug":"example","title":"Serie"}],"last_page":3};</script>"#}))
        .route("/example/",get(||async{r#"<h1>Serie</h1><span data-anime="42"></span>"#}))
        .route("/",get(||async{([("set-cookie","session=local; Path=/")],r#"<meta name="csrf-token" content="fixture">"#)}))
        .route("/ajax/episodes/42/{page}",post(|axum::extract::Path(n):axum::extract::Path<u32>,h:axum::http::HeaderMap,Form(form):Form<HashMap<String,String>>|async move{
            assert_eq!(form["_token"],"fixture");assert_eq!(h["cookie"],"session=local");
            Json(json!({"data":[{"number":n,"title":"Episodio"}],"last_page":2}))
        }));
    let mut src = source("jkanime", router).await;
    let page = src.page(Some("2")).await.unwrap();
    assert_eq!(page.next.as_deref(), Some("3"));
    assert_eq!(page.items[0].external_id, "example");
    let item = src.detail(&page.items[0]).await.unwrap();
    assert_eq!(item.metadata["units"][1]["displayNumber"], "2");
}

#[tokio::test]
async fn doramas_series_resumes_into_movies_and_resolves_a_season() {
    let router=Router::new()
        .route("/api/gql",post(|Json(q):Json<Value>|async move{
            let data=match q["operationName"].as_str().unwrap(){
                "listDoramas"=>json!({"paginationDorama":{"items":[{"_id":"abc","slug":"serie","name_es":"Serie"}],"pageInfo":{"hasNextPage":false}}}),
                "listSeasons"=>{assert_eq!(q["variables"]["serie_id"],"abc");json!({"listSeasons":[{"season_number":2}]})},
                "listEpisodes"=>{assert_eq!(q["variables"]["season_number"],2);json!({"listEpisodes":[{"episode_number":4,"slug":"s2e4","name":"Final"}]})},
                other=>panic!("unexpected operation {other}")
            };Json(json!({"data":data}))
        }))
        .route("/doramas-online/serie",get(||async{r#"<h1>Serie</h1><script type="application/json">{"serie_id":"abc"}</script>"#}))
        .route("/episodios/s2e4",get(||async{r#"<video src="https://media.example/show.mp4"></video>"#}));
    let mut src = source("doramasflix", router).await;
    let page = src.page(None).await.unwrap();
    assert_eq!(page.next.as_deref(), Some("movies:1"));
    let mut entry = page.items.into_iter().next().unwrap();
    entry
        .preview
        .as_object_mut()
        .unwrap()
        .remove("providerSeriesId");
    let item = src.detail(&entry).await.unwrap();
    assert_eq!(
        item.metadata["seasons"][0]["units"][0]["displayNumber"],
        "4"
    );
    assert_eq!(
        src.doramas_streams(&entry.external_id, Some(2), "4")
            .await
            .unwrap()[0]["url"],
        "https://media.example/show.mp4"
    );
}

#[tokio::test]
async fn retry_after_and_access_denials_reach_crawler() {
    let router = Router::new().route(
        "/api/discover",
        get(|Query(q): Query<HashMap<String, String>>| async move {
            if q["page"] == "2" {
                (StatusCode::TOO_MANY_REQUESTS, [("retry-after", "38")])
            } else {
                (StatusCode::FORBIDDEN, [("retry-after", "0")])
            }
        }),
    );
    let mut src = source("framextv", router).await;
    let e = src.page(Some("movie:2")).await.err().unwrap();
    assert_eq!((e.kind, e.retry), ("throttled", 38));
    assert_eq!(
        src.page(None).await.err().unwrap().kind,
        "challenge_or_access_denied"
    );
}

#[tokio::test]
async fn framex_carries_dash_subtitles_and_an_explicit_site_player() {
    let router=Router::new().route("/api/stream",get(||async{
        Json(json!({"success":true,"sources":[{"type":"dash","url":"https://media.example/asset.mpd"}],"subtitles":[{"lang":"es","url":"https://media.example/es.vtt","isDefault":true}]}))
    }));
    let mut src = source("framextv", router).await;
    let options = src.framex_streams("movie/13", None, 1).await.unwrap();
    assert_eq!(options[0]["dash"], true);
    assert_eq!(options[0]["tracks"][0]["language"], "es");
    assert_eq!(options.last().unwrap()["external"], true);
    assert!(src.framex_streams("tv/13", Some(0), 1).await.is_ok());
    assert!(src.framex_streams("tv/13", Some(0), 0).await.is_err());
    assert!(src.framex_streams("tv/13", None, 1).await.is_err());
}

#[tokio::test]
async fn tioplus_movie_directory_hands_over_to_series_and_returns_a_playable_source() {
    let router = Router::new()
        .route(
            "/peliculas",
            get(|| async { r#"<a href="/pelicula/ejemplo"><img alt="Ejemplo"></a>"# }),
        )
        .route(
            "/pelicula/ejemplo",
            get(|| async {
                r#"<h1>Ejemplo</h1><video src="https://media.example/ejemplo.mp4"></video>"#
            }),
        );
    let mut src = source("pelisplus-to", router).await;
    let page = src.page(None).await.unwrap();
    assert_eq!(page.next.as_deref(), Some("series:1"));
    let entry = &page.items[0];
    assert_eq!(src.detail(entry).await.unwrap().name, "Ejemplo");
    assert_eq!(
        src.cinema_streams(&entry.external_id, None, "1")
            .await
            .unwrap()[0]["url"],
        "https://media.example/ejemplo.mp4"
    );
}
