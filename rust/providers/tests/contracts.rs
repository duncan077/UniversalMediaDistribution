use axum::{
    extract::Query,
    http::{header, HeaderMap, StatusCode},
    routing::{get, post},
    Form, Json, Router,
};
use reqwest::Url;
use serde_json::{json, Value};
use std::{collections::HashMap, time::Duration};
use umd_providers::{browser_http, Manga, Provider, Site};
async fn source(site: &str, router: Router) -> Provider {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let addr = listener.local_addr().unwrap();
    tokio::spawn(async move { axum::serve(listener, router).await.unwrap() });
    let base = Url::parse(&format!("http://fixture.example:{}/", addr.port())).unwrap();
    let client = browser_http::client_builder()
        .cookie_store(true)
        .no_proxy()
        .redirect(reqwest::redirect::Policy::none())
        .resolve("fixture.example", addr)
        .build()
        .unwrap();
    Provider::new(Site::parse(site).unwrap(), base, client, Duration::ZERO)
}

#[tokio::test]
async fn zonatmo_extracts_same_named_novel_and_manhwa_as_distinct_editions() {
    let router = Router::new()
        .route("/biblioteca", get(|| async {
            r#"<a href="/library/novel/191/obra"><h4>Obra</h4></a>
                <a href="/library/manhwa/23158/obra"><h4>Obra</h4><span class="book-type">MANHWA</span></a>"#
        }))
        .route("/library/novel/191/obra/", get(|| async {
            r#"<h1 class="element-title">Obra</h1><h1 class="book-type">NOVEL</h1>
                <li class="upload-link" data-chapter-number="185"><a href="/view_uploads/185">Leer</a></li>"#
        }))
        .route("/library/manhwa/23158/obra/", get(|| async {
            r#"<h1 class="element-title">Obra</h1><h1 class="book-type">MANHWA</h1>
                <li class="upload-link" data-chapter-number="97"><a href="/view_uploads/97">Leer</a></li>"#
        }));
    let mut provider = source("zonatmo", router).await;
    let rows = provider.search("Obra", 1).await.unwrap().items;
    assert_eq!(rows.len(), 2);
    assert_eq!(rows[0].preview["subtype"], "novel");
    assert_eq!(rows[1].preview["subtype"], "manga");
    let novel = provider.detail(&rows[0]).await.unwrap();
    let manhwa = provider.detail(&rows[1]).await.unwrap();
    assert_ne!(novel.id().unwrap(), manhwa.id().unwrap());
    assert_eq!(novel.metadata["units"][0]["displayNumber"], "185");
    assert_eq!(manhwa.metadata["units"][0]["displayNumber"], "97");
    let error = provider
        .manga_images(Manga::Zona, &rows[0].external_id, "185")
        .await
        .unwrap_err();
    assert_eq!(error.kind, "unsupported");
}

#[tokio::test]
async fn uchuujin_classifies_untyped_cards_from_the_detail_type() {
    let router = Router::new()
        .route("/", get(|| async { r#"<a href="/manga/obra"><h4>Obra</h4></a>"# }))
        .route("/manga/obra/", get(|| async {
            r#"<h1 class="entry-title">Obra</h1><div class="imptdt"><strong>Tipo:</strong> Novela ligera</div>"#
        }));
    let mut provider = source("uchuujinmangas", router).await;
    let search = provider.search("Obra", 1).await.unwrap();
    let entry = &search.items[0];
    assert!(entry.preview["subtype"].is_null());
    let item = provider.detail(entry).await.unwrap();
    assert_eq!(item.subtype, "light_novel");
    assert_eq!(item.metadata["categories"], json!(["Novela ligera"]));
}

#[tokio::test]
async fn provider_sends_browser_headers_and_accepts_the_requested_format() {
    fn assert_browser_headers(headers: &HeaderMap) {
        let agent = headers[header::USER_AGENT].to_str().unwrap();
        assert!(agent.starts_with("Mozilla/5.0 (Windows NT 10.0; Win64; x64)"));
        assert!(agent.contains("Chrome/"));
        assert!(agent.ends_with(" Safari/537.36"));
        assert_eq!(headers[header::ACCEPT_LANGUAGE], "es-ES,es;q=0.9,en;q=0.7");
    }

    let router = Router::new().route(
        "/directorio",
        get(|headers: HeaderMap| async move {
            assert_browser_headers(&headers);
            assert_eq!(headers[header::ACCEPT], browser_http::PAGE_ACCEPT);
            r#"<script>var animes = {"data":[{"slug":"example","title":"Serie"}],"last_page":1};</script>"#
        }),
    );
    let mut html = source("jkanime", router).await;
    assert_eq!(html.page(None).await.unwrap().items.len(), 1);

    let router = Router::new().route(
        "/api/discover",
        get(|headers: HeaderMap| async move {
            assert_browser_headers(&headers);
            assert_eq!(headers[header::ACCEPT], "application/json");
            StatusCode::TOO_MANY_REQUESTS
        }),
    );
    let mut api = source("framextv", router).await;
    assert_eq!(api.page(None).await.err().unwrap().kind, "throttled");
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
                "latestTitles"=>json!({"series":{"items":[{"slug":"serie","name":"Serie"}]},"movies":{"items":[{"slug":"filme","name":"Filme"}]}}),
                "listSeasons"=>{assert_eq!(q["variables"]["serie_id"],"abc");json!({"listSeasons":[{"season_number":2},{"season_number":3}]})},
                "seasonEpisodes"=>{
                    assert_eq!(q["variables"],json!({"serie_id":"abc","n0":2,"n1":3}));
                    assert!(q["query"].as_str().unwrap().contains("s1:listEpisodes"));
                    json!({"s0":[{"episode_number":4,"slug":"s2e4","name":"Final"}],"s1":[{"episode_number":1,"slug":"s3e1","name":"Regreso"}]})
                },
                other=>panic!("unexpected operation {other}")
            };Json(json!({"data":data}))
        }))
        .route("/doramas-online/serie",get(||async{r#"<h1>Serie</h1><script type="application/json">{"serie_id":"abc"}</script>"#}))
        .route("/episodios/s2e4",get(||async{r#"<video src="https://media.example/show.mp4"></video>"#}));
    let mut src = source("doramasflix", router).await;
    assert_eq!(src.updates().await.unwrap().len(), 2);
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
        item.metadata["seasons"][1]["units"][0]["displayNumber"],
        "1"
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
