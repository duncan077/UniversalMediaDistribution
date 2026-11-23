use super::*;
use std::cell::Cell;

#[tokio::test]
async fn resolves_public_wrapper_to_videojs_dash_with_subtitles() {
    let wrapper = Url::parse("https://upzone.to/e/title").unwrap();
    let stream=resolve_pages(wrapper, async |url,_parent| {
        let html=match url.path(){
            "/e/title"=>r#"<iframe src="/player"></iframe>"#,
            "/player"=>r#"<video data-setup='{"sources":[{"src":"/manifest?id=5","type":"application/dash+xml"}],"tracks":[{"src":"subs.vtt","kind":"captions","srclang":"es","label":"Español","default":true}]}'></video>"#,
            _=>panic!("Unexpected request"),
        };
        Ok(Page::Html(url,html.into()))
    }).await.unwrap();
    assert!(stream.dash && !stream.hls);
    assert_eq!(stream.url, "https://upzone.to/manifest?id=5");
    assert_eq!(stream.tracks[0].url, "https://upzone.to/subs.vtt");
    assert_eq!(stream.tracks[0].language, "es");
    assert!(stream.tracks[0].default);
}

#[tokio::test]
async fn typed_embeds_and_plain_hoster_configurations() {
    let start =
        Url::parse("https://nupload.top/iframe/?url=https%3A%2F%2Fvoe.sx%2Fe%2Fshow").unwrap();
    let stream = resolve_pages(start, async |url, _| {
        assert_eq!(url.as_str(), "https://voe.sx/e/show");
        Ok(Page::Html(
            url,
            "<script>var sources={'hls':'/media/master.m3u8'};</script>".into(),
        ))
    })
    .await
    .unwrap();
    assert!(stream.hls);
    let base = Url::parse("https://www.mp4upload.com/embed-film.html").unwrap();
    let document = Document::from(
        r#"<script>player.src({type:'video/mp4',src:'https://cdn.example/film.mp4'});</script>"#,
    );
    assert_eq!(
        extract(&base, &document).unwrap().url,
        "https://cdn.example/film.mp4"
    );
    let page = Document::from(
        r#"<script type="application/ld+json">{"@graph":[{"@type":"ImageObject","contentUrl":"wrong.mp4"},{"@type":"VideoObject","contentUrl":"/encoded","encodingFormat":"video/webm"}]}</script>"#,
    );
    assert_eq!(
        extract(&base, &page).unwrap().url,
        base.join("/encoded").unwrap().as_str()
    );
    let page = Document::from(
        r#"<video src="/film.mp4"><track kind="metadata" src="/stats"><track kind="subtitles" srclang="es" src="/es.vtt"><track src="http://127.0.0.1/track"></video><video><track src="/other.vtt"></video>"#,
    );
    let found = extract(&base, &page).unwrap();
    assert_eq!(found.tracks.len(), 1);
    assert_eq!(found.tracks[0].url, base.join("/es.vtt").unwrap().as_str());
}

#[tokio::test]
async fn wrapper_resolution_bounds_requests_and_rejects_ambiguous_or_private_frames() {
    let base = Url::parse("https://movie.example/show").unwrap();
    for (html, limit) in [
        ("<iframe src='/ad'></iframe>", 1),
        (
            "<iframe allowfullscreen src='http://127.0.0.1/'></iframe>",
            1,
        ),
        (
            "<iframe allowfullscreen src='/a'></iframe><iframe allowfullscreen src='/b'></iframe>",
            1,
        ),
        ("<iframe allowfullscreen src='/show#video'></iframe>", 1),
        ("<iframe allowfullscreen src='new/'></iframe>", 3),
    ] {
        let calls = Cell::new(0);
        let result = resolve_pages(base.clone(), async |url, _| {
            calls.set(calls.get() + 1);
            Ok(Page::Html(url, html.into()))
        })
        .await;
        assert!(result.is_err());
        assert_eq!(calls.get(), limit);
    }
}

#[test]
fn guides_follow_catalog_navigation_and_skip_unrelated_links() {
    let base = Url::parse("https://tv.example/guide").unwrap();
    let (channels, next) = grid_channels(
        &base,
        r#"<a href="/schedule">Ver canales</a><a href="/one"><img alt="Channel One"/></a><a href="/one">Again</a><a href="https://elsewhere.example/watch">Other origin</a>"#,
    );
    assert_eq!(channels.len(), 1);
    assert_eq!(channels[0].name, "Channel One");
    assert_eq!(next.unwrap().path(), "/schedule");
}
#[test]
fn zilla_accepts_only_the_documented_player_route() {
    assert_eq!(
        zilla_playlist(&Url::parse("https://player.zilla-networks.com/play/news_9").unwrap())
            .unwrap()
            .path(),
        "/m3u8/news_9"
    );
    assert!(zilla_playlist(&Url::parse("https://other.example/play/news_9").unwrap()).is_none());
    assert!(zilla_playlist(
        &Url::parse("https://player.zilla-networks.com/play/../secret").unwrap()
    )
    .is_none());
}

#[tokio::test]
async fn zilla_requires_media_bytes_after_following_a_master_playlist() {
    use reqwest::ResponseBuilderExt;
    let root = Url::parse("https://player.zilla-networks.com/m3u8/movie").unwrap();
    for accessible in [false, true] {
        let requested_segment = Cell::new(false);
        let requested = &requested_segment;
        let result = zilla_stream(root.clone(), |url: Url, probe| async move {
            let data = match url.path() {
                "/m3u8/movie" => "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2000\nvideo.m3u8",
                "/m3u8/video.m3u8" => "#EXTM3U\n#EXTINF:5,\nsegment.ts",
                "/m3u8/segment.ts" => {
                    assert!(probe);
                    requested.set(true);
                    if !accessible {
                        return Err("HTTP 403".into());
                    }
                    "G"
                }
                unexpected => panic!("unexpected request {unexpected}"),
            };
            Ok(axum::http::Response::builder()
                .url(url)
                .body(reqwest::Body::from(data))
                .unwrap()
                .into())
        })
        .await;
        assert!(requested_segment.get());
        assert_eq!(result.is_ok(), accessible);
        if let Ok(stream) = result {
            assert_eq!(stream.url, root.as_str());
            assert!(stream.hls);
        }
    }
}

#[test]
fn hosted_players_read_static_sources_without_running_scripts() {
    for (host, html, expected) in [
        (
            "mixdrop.to",
            r#"<script>MDCore.wurl = "//cdn.example.org/film.mp4"</script>"#,
            "https://cdn.example.org/film.mp4",
        ),
        (
            "streamtape.com",
            r#"<span id="robotlink">//cdn.example.org/get_video?id=15</span>"#,
            "https://cdn.example.org/get_video?id=15",
        ),
        (
            "filemoon.sx",
            r#"<script>player.setup({sources: [{"file": "/h/master.m3u8"}]})</script>"#,
            "https://filemoon.sx/h/master.m3u8",
        ),
    ] {
        let base = Url::parse(&format!("https://{host}/e/film")).unwrap();
        assert_eq!(extract(&base, &Document::from(html)).unwrap().url, expected);
    }
}
