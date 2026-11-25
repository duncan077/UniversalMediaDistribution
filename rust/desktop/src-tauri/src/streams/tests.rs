use super::*;
use base64::{engine::general_purpose::STANDARD, Engine};
use std::cell::Cell;

#[tokio::test]
async fn mega_file_links_keep_the_share_key_for_the_official_player() {
    let id = "aB1_cdE2";
    let key = "a".repeat(43);
    let expected = format!("https://mega.nz/embed/{id}#{key}");
    for address in [
        format!("https://mega.nz/file/{id}#{key}"),
        format!("https://mega.co.nz/#!{id}!{key}"),
        expected.clone(),
    ] {
        assert_eq!(
            provider_web_address(Url::parse(&address).unwrap())
                .await
                .unwrap()
                .as_str(),
            expected
        );
    }
    let folder = Url::parse(&format!("https://mega.nz/folder/{id}#{key}")).unwrap();
    assert!(hosters::mega_embed(&folder).is_none());
    let spoof = Url::parse(&format!("https://notmega.nz/file/{id}#{key}")).unwrap();
    assert!(hosters::mega_embed(&spoof).is_none());
}

#[tokio::test]
async fn upnshare_reference_reaches_the_public_resolver_and_keeps_original_on_error() {
    let original = Url::parse("https://animeav1.uns.bio/#abc123").unwrap();
    assert_eq!(
        hosters::upnshare_endpoint(&original).unwrap().as_str(),
        "https://animeav1.uns.bio/api/public/resolve?file1=abc123"
    );
    assert!(
        hosters::upnshare_endpoint(&Url::parse("https://elsewhere.uns.bio/#abc123").unwrap())
            .is_none()
    );
    let mut browser = Some(original.clone());
    let result = resolve_pages(original.clone(), None, &mut browser, async |url, _| {
        assert_eq!(url.fragment(), Some("abc123"));
        Err("HTTP 404".into())
    })
    .await;
    assert_eq!(result.unwrap_err(), "HTTP 404");
    assert_eq!(browser, Some(original));
}

#[tokio::test]
async fn upnshare_only_follows_public_targets_from_its_resolver() {
    let page = Url::parse("https://animeav1.uns.bio/#abc123").unwrap();
    for (target, expected) in [
        ("https://media.example/video.m3u8?public=short", true),
        ("https://127.0.0.1/video.m3u8", false),
    ] {
        let response = Url::parse(target).unwrap();
        let result = resolve_pages(page.clone(), None, &mut None, async |url, _| {
            assert_eq!(url, page);
            Ok(Page::Redirect(url, response.clone()))
        })
        .await;
        assert_eq!(result.is_ok(), expected);
        if let Ok(stream) = result {
            assert_eq!(stream.url, target);
            assert_eq!(stream.referer.as_deref(), Some("https://animeav1.uns.bio/"));
        }
    }
}

#[tokio::test]
async fn upnshare_falls_back_to_the_delegated_player_and_rejects_removed_videos() {
    let original = Url::parse("https://animeav1.uns.bio/#abc123").unwrap();
    let delegated = Url::parse("https://turboembed.com/test").unwrap();
    let mut browser = Some(original.clone());
    let _ = resolve_pages(original.clone(), None, &mut browser, async |url, _| {
        if url == original {
            Ok(Page::Redirect(url, delegated.clone()))
        } else {
            assert_eq!(url, delegated);
            Err("Verification required".into())
        }
    })
    .await;
    assert_eq!(browser, Some(delegated));

    let mut browser = Some(original.clone());
    let result = resolve_pages(original, None, &mut browser, async |_, _| {
        Ok(Page::ProviderUnavailable("Video unavailable"))
    })
    .await;
    assert_eq!(result.unwrap_err(), "Video unavailable");
    assert!(browser.is_none());
}

#[tokio::test]
async fn poseidon_uses_the_final_voe_player_if_it_requires_verification() {
    let original = Url::parse("https://player.poseidonhd2.co/player/1").unwrap();
    let mut browser = Some(original.clone());
    let result = resolve_pages(original, None, &mut browser, async |url, _| {
        let html = match url.host_str().unwrap() {
            "player.poseidonhd2.co" => r#"<script>var url = "https://voe.sx/e/test";</script>"#,
            "voe.sx" => r#"<script>location.href = "https://caseyseemavoid.com/e/test";</script>"#,
            "caseyseemavoid.com" => "<html>Confirm you're human</html>",
            host => panic!("Unexpected {host}"),
        };
        Ok(Page::Html(url, html.into()))
    })
    .await;
    assert!(result.is_err());
    assert_eq!(
        browser.unwrap().as_str(),
        "https://caseyseemavoid.com/e/test"
    );
}

#[tokio::test]
async fn resolves_public_wrapper_to_videojs_dash_with_subtitles() {
    let wrapper = Url::parse("https://upzone.to/e/title").unwrap();
    let stream=resolve_pages(wrapper, None, &mut None, async |url,_parent| {
        let html=match url.path(){
            "/e/title"=>r#"<iframe src="/player"></iframe>"#,
            "/player"=>r#"<video data-setup='{"sources":[{"src":"/manifest?id=5","type":"application/dash+xml"}],"tracks":[{"src":"subs.vtt","kind":"captions","srclang":"es","label":"Español","default":true}]}'></video>"#,
            _=>panic!("Unexpected request"),
        };
        Ok(Page::Html(url,html.into()))
    }).await.unwrap();
    assert!(stream.dash && !stream.hls);
    assert_eq!(stream.url, "https://upzone.to/manifest?id=5");
    assert_eq!(stream.referer.as_deref(), Some("https://upzone.to/player"));
    assert_eq!(stream.tracks[0].url, "https://upzone.to/subs.vtt");
    assert_eq!(stream.tracks[0].language, "es");
    assert!(stream.tracks[0].default);
}

#[tokio::test]
async fn typed_embeds_and_plain_hoster_configurations() {
    let start =
        Url::parse("https://nupload.my/iframe/?url=https%3A%2F%2Fvoe.sx%2Fe%2Fshow").unwrap();
    let stream = resolve_pages(start, None, &mut None, async |url, _| {
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
        let result = resolve_pages(base.clone(), None, &mut None, async |url, _| {
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
        let requested_init = Cell::new(false);
        let init = &requested_init;
        let result = zilla_stream(root.clone(), |url: Url, probe| async move {
            let data = match url.path() {
                "/m3u8/movie" => "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2000\nvideo.m3u8",
                "/m3u8/video.m3u8" => {
                    "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:5,\nsegment.ts"
                }
                "/m3u8/init.mp4" => {
                    assert!(probe);
                    init.set(true);
                    "I"
                }
                "/m3u8/segment.ts" => {
                    assert!(probe);
                    assert!(init.get());
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
        assert!(requested_init.get());
        assert!(requested_segment.get());
        assert_eq!(result.is_ok(), accessible);
        if let Ok(stream) = result {
            assert_eq!(stream.url, root.as_str());
            assert!(stream.hls);
        }
    }
}

#[tokio::test]
async fn a_streamtape_embed_filename_does_not_skip_extraction() {
    let address = Url::parse("https://streamtape.com/e/abcdef/video.mp4").unwrap();
    let stream = resolve_pages(address.clone(), None, &mut None, async |url, _| {
        assert_eq!(url, address);
        Ok(Page::Html(
            url,
            "<div id='robotlink'>//streamtape.com/get_video?id=abc</div>".into(),
        ))
    })
    .await
    .unwrap();
    assert_eq!(stream.url, "https://streamtape.com/get_video?id=abc");
    assert!(media(
        Url::parse("https://cdn.example/video.mp4").unwrap(),
        "text/html"
    )
    .is_none());
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
            "mxdrop.click",
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

#[test]
fn packed_player_formats_and_mirrors() {
    let html = r#"<script>eval(function(p,a,c,k,e,d){return p;}('0.1({2:[{3:"/h/master.m3u8"}]});',36,4,'player|setup|sources|file'.split('|'),0,{}))</script>"#;
    for host in [
        "filemoon.site",
        "byselapuix.com",
        "sfastwish.com",
        "vidhidepro.com",
        "sbembed.com",
        "streamlare.com",
        "ninjastream.to",
        "uch.to",
        "uqload.cx",
        "vidmoly.me",
        "mirror.example",
    ] {
        let base = Url::parse(&format!("https://{host}/e/film")).unwrap();
        let stream = extract(&base, &Document::from(html)).unwrap();
        assert!(stream.hls);
        assert_eq!(stream.url, format!("https://{host}/h/master.m3u8"));
    }
    for value in ["http://127.7.7.7/private.m3u8", "file:///private.m3u8"] {
        let page = html.replace("/h/master.m3u8", value);
        let base = Url::parse("https://uqload.com/e/1").unwrap();
        assert!(extract(&base, &Document::from(page.as_str())).is_none());
    }
}

#[test]
fn streamtape_uses_the_assembled_link_before_a_stale_link_in_the_dom() {
    let base = Url::parse("https://streamtape.com/e/film").unwrap();
    let html = r#"<div id="robotlink">//streamtape.com/get_video?id=15&amp;token=wrong</div>
      <script>document.getElementById('robotlink').innerHTML = '//st' + ('xryreamtape.com/get_video?id=15&token=right').substring(2).substring(1);</script>"#;
    let stream = extract(&base, &Document::from(html)).unwrap();
    assert_eq!(
        stream.url,
        "https://streamtape.com/get_video?id=15&token=right"
    );
}

#[tokio::test]
async fn provider_wrapper_and_rotating_voe_mirror_keep_their_referrers() {
    let initial = Url::parse("https://tioplus.app/player/film").unwrap();
    let provider = Url::parse("https://tioplus.app/").unwrap();
    let source = "https://cdn.example/hls/master.m3u8";
    let json = serde_json::json!({"source": source}).to_string();
    let transformed: Vec<_> = STANDARD.encode(json).bytes().rev().map(|b| b + 3).collect();
    let payload = STANDARD
        .encode(transformed)
        .bytes()
        .map(|b| match b {
            b'A'..=b'Z' => (b'A' + (b - b'A' + 13) % 26) as char,
            b'a'..=b'z' => (b'a' + (b - b'a' + 13) % 26) as char,
            _ => b as char,
        })
        .collect::<String>();
    let stream = resolve_pages(initial, Some(provider), &mut None, async |url, parent| {
        let body = match url.host_str().unwrap() {
            "tioplus.app" => {
                assert_eq!(parent.path(), "/");
                r#"<script>const url = "https://voe.sx/e/film";</script>"#.to_owned()
            }
            "voe.sx" => {
                assert_eq!(parent.path(), "/player/film");
                r#"<script>location.href = "https://new-mirror.example/e/film";</script>"#
                    .to_owned()
            }
            "new-mirror.example" => {
                assert_eq!(parent.host_str(), Some("voe.sx"));
                format!(r#"<script type="application/json">["{payload}"]</script>"#)
            }
            host => panic!("Unexpected host {host}"),
        };
        Ok(Page::Html(url, body))
    })
    .await
    .unwrap();
    assert_eq!(stream.url, source);
    assert_eq!(
        stream.referer.as_deref(),
        Some("https://new-mirror.example/e/film")
    );
}

#[test]
fn dedicated_hosts_read_published_media_parameters() {
    let base = Url::parse("https://vixcloud.co/e/film?canPlayFHD=1").unwrap();
    let html = r#"<script>window.video={id: 77}; window.masterPlaylist={token:'a+b',expires:'1234',url:'https://vixcloud.co/playlist/77?b=1'};</script>"#;
    let stream = extract(&base, &Document::from(html)).unwrap();
    assert!(stream.hls);
    assert_eq!(
        stream.url,
        "https://vixcloud.co/playlist/77?token=a%2Bb&expires=1234&b=1&h=1"
    );
    let encoded = b"https://cdn.example/movie.m3u8?k=fixture"
        .iter()
        .map(|b| format!("'{}'", STANDARD.encode((u32::from(*b) + 33).to_string())))
        .collect::<Vec<_>>()
        .join(",");
    let html = format!(
        r#"<script>const a=[{encoded}]; var sesz="abc"; a.forEach(function(piece){{s+=String.fromCharCode(parseInt(atob(piece).replace(/\D/g,''))-33);}});</script>"#
    );
    let base = Url::parse("https://nupload.my/watch/movie").unwrap();
    let stream = extract(&base, &Document::from(html.as_str())).unwrap();
    assert!(stream.hls);
    assert_eq!(stream.url, "https://cdn.example/movie.m3u8?k=fixture&s=abc");
    let base = Url::parse("https://dsvplay.com/e/film").unwrap();
    let (endpoint, token) =
        hosters::protocols::dood_request(&base, "fetch('/pass_md5/abc/xyz')").unwrap();
    assert_eq!(endpoint.as_str(), "https://dsvplay.com/pass_md5/abc/xyz");
    assert!(hosters::protocols::dood_media(&base, "http://127.0.0.1/", &token).is_none());
    let stream =
        hosters::protocols::dood_media(&base, "https://cdn.example/media/", &token).unwrap();
    assert!(
        stream.url.starts_with("https://cdn.example/media/")
            && stream.url.contains("?token=xyz&expiry=")
    );
}

#[test]
fn delegated_players_choose_the_media_frame() {
    let base = Url::parse("https://upzone.to/watch/movie").unwrap();
    let doc = Document::from(
        r#"<iframe src="https://ad.example/overlay"></iframe><iframe src="https://uqload.cx/e/film"></iframe>"#,
    );
    assert_eq!(
        declared::embed(&base, &doc).unwrap().as_str(),
        "https://uqload.cx/e/film"
    );
    let base = Url::parse("https://frembed.click/e/film").unwrap();
    let json = r#"{"link1":"https://ad.example/overlay","link2":"https://sfastwish.com/e/film","link3":"https://127.0.0.1/"}"#;
    assert_eq!(
        hosters::protocols::frembed(&base, json).unwrap().as_str(),
        "https://sfastwish.com/e/film"
    );
    let base = Url::parse("https://loadx.top/e/abc-123").unwrap();
    assert_eq!(
        hosters::protocols::loadx_endpoint(&base).unwrap().as_str(),
        "https://loadx.ws/player/index.php?data=abc-123&do=getVideo"
    );
}

#[test]
fn speedfiles_and_upzur_decode_without_executing_scripts() {
    let movie = "https://cdn.example/film.mp4";
    let swap = |b: u8| {
        if b.is_ascii_uppercase() {
            b.to_ascii_lowercase()
        } else {
            b.to_ascii_uppercase()
        }
    };
    let encoded = STANDARD.encode(movie);
    let hex = encoded
        .bytes()
        .rev()
        .map(|b| format!("{:02x}", swap(b) + 3))
        .collect::<String>();
    let reversed: Vec<_> = hex.bytes().rev().collect();
    let payload = STANDARD.encode(
        STANDARD
            .encode(reversed)
            .bytes()
            .rev()
            .map(swap)
            .collect::<Vec<_>>(),
    );
    let html = format!(r#"<script>var _0x5opu234="{payload}";</script>"#);
    let base = Url::parse("https://speedfiles.net/e/film").unwrap();
    assert_eq!(
        extract(&base, &Document::from(html.as_str())).unwrap().url,
        movie
    );

    let base = Url::parse("https://upzur.com/e/film").unwrap();
    let html = r#"<script>var uHo4sc = ['\x3c/video>', '<video><source src="https://cdn.example/film.mp4">\x3e'];</script>"#;
    assert_eq!(extract(&base, &Document::from(html)).unwrap().url, movie);
}
#[test]
fn tv_grid_keeps_labeled_signals_and_hls_resolutions() {
    let page = Url::parse("https://example.com/channel").unwrap();
    let links = live_server_links(
        &page,
        r#"
        <a href="/first">Opción HD</a><button data-src="/second">Servidor FHD</button>
        <a href="/first">Servidor duplicado</a><a href="/unrelated">Noticias</a>
        <button data-url="http://127.0.0.1/private">Servidor privado</button>"#,
    );
    assert_eq!(
        links.iter().map(|(_, url)| url.path()).collect::<Vec<_>>(),
        ["/first", "/second"]
    );
    let variants = live_hls_qualities(&page, "#EXTM3U\n#EXT-X-STREAM-INF:RESOLUTION=640x360\nlo.m3u8\n#EXT-X-STREAM-INF:RESOLUTION=1920x1080\nhi.m3u8\n#EXT-X-STREAM-INF:RESOLUTION=1280x720,AUDIO=\"external\"\nneeds-master.m3u8");
    assert_eq!(
        variants
            .iter()
            .map(|(height, _)| *height)
            .collect::<Vec<_>>(),
        [1080, 360]
    );
    assert_eq!(variants[0].1.path(), "/hi.m3u8");
}
