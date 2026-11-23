use super::{media, web_url, Stream, Subtitle};
use dom_query::{Document, Selection};
use reqwest::Url;
use serde_json::Value;

fn address(base: &Url, raw: &str, kind: &str) -> Option<Url> {
    if raw.trim().is_empty() || raw.len() > 16_384 {
        return None;
    }
    let url = base.join(raw.trim()).ok()?;
    (web_url(&url) && !crate::ad_filter::blocked(url.as_str(), base.as_str(), kind, "GET"))
        .then_some(url)
}

fn stream(base: &Url, raw: &str, mime: &str) -> Option<Stream> {
    media(address(base, raw, "media")?, mime)
}

fn subtitle(base: &Url, src: &str, lang: &str, label: &str, default: bool) -> Option<Subtitle> {
    let url = address(base, src, "media")?;
    Some(Subtitle {
        url: url.into(),
        language: lang.to_owned(),
        label: if label.trim().is_empty() { lang } else { label }.to_owned(),
        default,
    })
}

fn html_tracks(base: &Url, player: &Selection<'_>) -> Vec<Subtitle> {
    player
        .select("track[src]")
        .iter()
        .filter_map(|el| {
            let kind = el.attr_or("kind", "subtitles");
            if !matches!(kind.as_ref(), "captions" | "subtitles") {
                return None;
            }
            subtitle(
                base,
                &el.attr("src")?,
                &el.attr_or("srclang", "und"),
                &el.attr_or("label", ""),
                el.attr("default").is_some(),
            )
        })
        .take(100)
        .collect()
}

fn data_setup(base: &Url, player: &Selection<'_>) -> Option<Stream> {
    let config: Value = serde_json::from_str(&player.attr("data-setup")?).ok()?;
    let mut found = config["sources"]
        .as_array()?
        .iter()
        .find_map(|s| stream(base, s["src"].as_str()?, s["type"].as_str().unwrap_or("")))?;
    found.tracks = config["tracks"]
        .as_array()
        .into_iter()
        .flatten()
        .filter(|t| matches!(t["kind"].as_str(), None | Some("captions" | "subtitles")))
        .filter_map(|t| {
            subtitle(
                base,
                t["src"].as_str()?,
                t["srclang"].as_str().unwrap_or("und"),
                t["label"].as_str().unwrap_or(""),
                t["default"] == true,
            )
        })
        .take(100)
        .collect();
    Some(found)
}

fn video_objects(value: &Value) -> impl Iterator<Item = &Value> {
    let roots = value
        .as_array()
        .map(Vec::as_slice)
        .unwrap_or(std::slice::from_ref(value));
    roots
        .iter()
        .flat_map(|v| std::iter::once(v).chain(v["@graph"].as_array().into_iter().flatten()))
        .filter(|v| {
            let kind = &v["@type"];
            let is_video = |s: &str| {
                matches!(
                    s,
                    "VideoObject"
                        | "https://schema.org/VideoObject"
                        | "http://schema.org/VideoObject"
                )
            };
            kind.as_str().is_some_and(is_video)
                || kind
                    .as_array()
                    .is_some_and(|a| a.iter().filter_map(Value::as_str).any(is_video))
        })
}

fn json_ld(doc: &Document) -> impl Iterator<Item = Value> + '_ {
    doc.select(r#"script[type="application/ld+json"]"#)
        .iter()
        .filter_map(|s| serde_json::from_str(&s.text()).ok())
        .collect::<Vec<_>>()
        .into_iter()
}

pub(super) fn extract(base: &Url, doc: &Document) -> Option<Stream> {
    for player in doc.select("video,audio").iter() {
        let direct = player
            .attr("src")
            .and_then(|s| stream(base, &s, &player.attr_or("type", "")))
            .or_else(|| {
                player
                    .select("source[src]")
                    .iter()
                    .find_map(|el| stream(base, &el.attr("src")?, &el.attr_or("type", "")))
            });
        if let Some(mut found) = direct.or_else(|| data_setup(base, &player)) {
            let tracks = html_tracks(base, &player);
            if !tracks.is_empty() {
                found.tracks = tracks;
            }
            return Some(found);
        }
    }
    // Static media bindings published by these players. Never execute host scripts.
    let host = base.host_str().unwrap_or("");
    let hosts = [
        "mixdrop.co",
        "mixdrop.ag",
        "mixdrop.to",
        "mixdrop.is",
        "streamtape.com",
        "streamtape.to",
        "filemoon.sx",
        "filemoon.to",
        "streamwish.to",
        "streamwish.com",
        "vidhide.com",
        "vidhideplus.com",
        "mp4upload.com",
        "doodstream.com",
        "dood.watch",
        "dood.to",
        "voe.sx",
    ];
    if hosts.iter().any(|h| {
        host == *h
            || host
                .strip_suffix(h)
                .is_some_and(|prefix| prefix.ends_with('.'))
    }) {
        static SOURCE: std::sync::LazyLock<regex::Regex> = std::sync::LazyLock::new(|| {
            regex::Regex::new(r#"(?i)(?:\bMDCore\.wurl\s*=|\b(?:sources|playlist)\s*:\s*\[\s*\{\s*["']?(?:file|src)["']?\s*:)\s*["']([^\s"'<>]+)["']"#).unwrap()
        });
        for el in doc.select("script:not([src])").iter() {
            for c in SOURCE.captures_iter(&el.text()) {
                if let Some(hit) = stream(base, &c[1].replace("\\/", "/"), "") {
                    return Some(hit);
                }
            }
        }
        for node in doc
            .select("a#download-video[href],#robotlink,#ideoooolink")
            .iter()
        {
            let raw = node.attr("href").unwrap_or_else(|| node.text());
            if raw.trim_start().starts_with("//") || raw.trim_start().starts_with("https://") {
                if let Some(hit) = stream(base, raw.trim(), "video/mp4") {
                    return Some(hit);
                }
            }
        }
    }
    for data in json_ld(doc) {
        if let Some(found) = video_objects(&data).find_map(|v| {
            stream(
                base,
                v["contentUrl"].as_str()?,
                v["encodingFormat"].as_str().unwrap_or(""),
            )
        }) {
            return Some(found);
        }
    }
    doc.select(r#"meta[property="og:video:secure_url"],meta[property="og:video:url"],meta[property="og:video"],meta[name="twitter:player:stream"]"#)
        .iter().find_map(|el|stream(base,&el.attr("content")?,""))
}

// Follow only a declared player, or one unambiguous fullscreen iframe.
// Ordinary links, tracking frames and ambiguous provider pickers stay on the site.
pub(super) fn embed(base: &Url, doc: &Document) -> Option<Url> {
    for data in json_ld(doc) {
        if let Some(url) =
            video_objects(&data).find_map(|v| address(base, v["embedUrl"].as_str()?, "subdocument"))
        {
            return Some(url);
        }
    }
    if let Some(url) = doc
        .select(r#"meta[name="twitter:player"]"#)
        .attr("content")
        .and_then(|v| address(base, &v, "subdocument"))
    {
        return Some(url);
    }
    let selector = if super::matches_host(
        base,
        &[
            "upzone.cc",
            "upzone.to",
            "upzone.net",
            "upzone.link",
            "nupload.top",
            "nupload.me",
        ],
    ) {
        "iframe[src]"
    } else {
        "iframe[allowfullscreen][src], iframe#player[src], iframe[allow*=fullscreen][src]"
    };
    let frames = doc.select(selector);
    let mut urls = frames
        .iter()
        .filter_map(|el| address(base, &el.attr("src")?, "subdocument"));
    let first = urls.next()?;
    urls.all(|u| u == first).then_some(first)
}
