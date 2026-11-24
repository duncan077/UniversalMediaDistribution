use super::{declared, decoded_base64, matches_host, unescape_literal, Stream};
use dom_query::Document;
use regex::Regex;
use reqwest::Url;
use serde_json::Value;
use std::sync::LazyLock;

const HLS: &str = "application/vnd.apple.mpegurl";
const DOOD: &[&str] = &[
    "doodstream.com",
    "dood.watch",
    "dood.to",
    "dood.li",
    "d0000d.com",
    "playmogo.com",
    "dsvplay.com",
];
pub(in crate::streams) const NUPLOAD: &[&str] = &["nupload.top", "nupload.me", "nupload.my"];

static DOOD_PATH: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"["'](/pass_md5/[A-Za-z0-9_-]{1,256}/([A-Za-z0-9_-]{1,256}))["']"#).unwrap()
});
static NUPLOAD_DECODER: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"([a-zA-Z_$][\w$]{0,63})\s*\.forEach\s*\(\s*function[^{}]{0,96}\{(?s:.{0,256}?)String\s*\.\s*fromCharCode\s*\(\s*parseInt\s*\(\s*atob\s*\([^()]{1,64}\)\s*\.replace\s*\(\s*/\\D/g\s*,\s*['"]{2}\s*\)\s*\)\s*-\s*(\d{1,10})"#).unwrap()
});
static ARRAY: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?s)\b(?:var|let|const)\s+([a-zA-Z_$][\w$]{0,63})\s*=\s*\[([^\]]*)\]").unwrap()
});
static QUOTED: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"(?s)"((?:\\.|[^"\\])*)"|'((?:\\.|[^'\\])*)'"#).unwrap());
static SESSION: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"\b(?:var|let|const)\s+sesz\s*=\s*["']([A-Za-z0-9_-]{1,256})["']"#).unwrap()
});
static VIX_VIDEO: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?s)\bwindow\.video\s*=\s*\{([^{}]*)\}").unwrap());
static VIX_MASTER: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(?s)\bwindow\.masterPlaylist\s*=\s*\{([^{}]*)\}").unwrap());
static VIX_ID: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"\bid\b["']?\s*:\s*["']?(\d{1,20})\b"#).unwrap());
static VIX_FIELD: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"\b(token|expires|url)\b["']?\s*:\s*["']([^"'<>]{1,4096})["']"#).unwrap()
});
static IRON_PAYLOAD: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"\batob\s*\(\s*atob\s*\(\s*["']([A-Za-z0-9+/=]+)["']"#).unwrap());
static URL: LazyLock<Regex> = LazyLock::new(|| Regex::new(r#"https?://[^\s'"<>\\]+"#).unwrap());
static SPEEDFILES: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"(?i)\b_0x5opu234\s*=\s*["']([A-Za-z0-9+/=]+)["']"#).unwrap());

fn string_array(value: &str, max: usize) -> Option<Vec<&str>> {
    let excludes = |c: char| c != ',' && !c.is_ascii_whitespace();
    let mut at = 0;
    let mut strings = Vec::new();
    for caps in QUOTED.captures_iter(value) {
        let full = caps.get(0)?;
        if strings.len() >= max || value[at..full.start()].contains(excludes) {
            return None;
        }
        strings.push(caps.get(1).or_else(|| caps.get(2))?.as_str());
        at = full.end();
    }
    (!strings.is_empty() && !value[at..].contains(excludes)).then_some(strings)
}

fn nupload(base: &Url, code: &str) -> Option<Stream> {
    let decoder = NUPLOAD_DECODER.captures(code)?;
    let offset = decoder[2].parse::<u64>().ok()?;
    let array = ARRAY.captures_iter(code).find(|c| c[1] == decoder[1])?;
    let mut decoded = String::new();
    for part in string_array(array.get(2)?.as_str(), 4096)? {
        if part.len() > 256 {
            return None;
        }
        let number: String = decoded_base64(part)?
            .chars()
            .filter(char::is_ascii_digit)
            .collect();
        let character = number.parse::<u64>().ok()?.checked_sub(offset)?;
        if !(0x20..=0x7e).contains(&character) {
            return None;
        }
        decoded.push(character as u8 as char);
    }
    let candidate = Url::parse(&decoded).ok()?;
    let mut url = declared::address(base, candidate.as_str(), "media")?;
    if let Some(caps) = SESSION.captures(code) {
        url.query_pairs_mut().append_pair("s", &caps[1]);
    }
    super::super::media(url, HLS)
}

fn vixcloud(base: &Url, code: &str) -> Option<Stream> {
    let video = VIX_VIDEO.captures(code)?;
    let id = VIX_ID.captures(&video[1])?;
    let mut url = base.join(&format!("/playlist/{}", &id[1])).ok()?;
    let mut query = url.query_pairs_mut();
    if let Some(master) = VIX_MASTER.captures(code) {
        for field in VIX_FIELD.captures_iter(&master[1]) {
            let value = unescape_literal(&field[2])?.replace("&amp;", "&");
            if &field[1] == "url" {
                if base
                    .join(&value)
                    .ok()
                    .is_some_and(|url| url.query_pairs().any(|(k, v)| k == "b" && v == "1"))
                {
                    query.append_pair("b", "1");
                }
            } else {
                query.append_pair(&field[1], &value);
            }
        }
    }
    if base
        .query_pairs()
        .any(|(k, _)| k.eq_ignore_ascii_case("canPlayFHD"))
    {
        query.append_pair("h", "1");
    }
    drop(query);
    declared::stream(base, url.as_str(), HLS)
}

fn iron_payload(base: &Url, code: &str) -> Option<Stream> {
    let first = IRON_PAYLOAD.captures(code)?;
    let encoded = first.get(1)?.as_str();
    if encoded.len() > 512 << 10 {
        return None;
    }
    let reduced: Vec<_> = decoded_base64(encoded)?
        .bytes()
        .map(|b| b.checked_sub(1))
        .collect::<Option<_>>()?;
    let script = decoded_base64(std::str::from_utf8(&reduced).ok()?)?;
    for url in URL
        .find_iter(&script)
        .filter_map(|v| Url::parse(v.as_str()).ok())
    {
        let mime = match base.path().rsplit('/').next()? {
            "face.php"
                if matches_host(&url, &["ironhentai.com"])
                    && url.path().ends_with("/hugging.php") =>
            {
                "video/mp4"
            }
            "vt.php" if url.path().ends_with(".m3u8") => HLS,
            _ => continue,
        };
        if let Some(source) = declared::stream(base, url.as_str(), mime) {
            return Some(source);
        }
    }
    None
}

fn speedfiles(base: &Url, code: &str) -> Option<Stream> {
    let caps = SPEEDFILES.captures(code)?;
    let payload = caps.get(1)?.as_str();
    if payload.len() > 512 << 10 {
        return None;
    }
    let swap = |c: char| {
        if c.is_ascii_uppercase() {
            c.to_ascii_lowercase()
        } else {
            c.to_ascii_uppercase()
        }
    };
    let reverse_case: String = decoded_base64(payload)?.chars().rev().map(swap).collect();
    let hex: Vec<_> = decoded_base64(&reverse_case)?.bytes().rev().collect();
    if !hex.len().is_multiple_of(2) {
        return None;
    }
    let mut source = String::new();
    for pair in hex.chunks_exact(2).rev() {
        let value = u8::from_str_radix(std::str::from_utf8(pair).ok()?, 16).ok()?;
        source.push(swap(value.checked_sub(3)? as char));
    }
    let decoded = decoded_base64(&source)?;
    let url = Url::parse(decoded.trim()).ok()?;
    if url.scheme() != "https" {
        return None;
    }
    declared::stream(base, url.as_str(), "")
}

pub(super) fn extract(base: &Url, code: &str) -> Option<Stream> {
    if matches_host(base, NUPLOAD) {
        return nupload(base, code);
    }
    if matches_host(base, &["vixcloud.co"]) {
        return vixcloud(base, code);
    }
    if matches_host(base, &["ironhentai.com"]) {
        return iron_payload(base, code);
    }
    if matches_host(base, &["speedfiles.net", "speedfiles.io"]) {
        return speedfiles(base, code);
    }
    if !matches_host(base, &["upzur.com"]) {
        return None;
    }
    let array = ARRAY.captures_iter(code).find(|c| &c[1] == "uHo4sc")?;
    let parts = string_array(array.get(2)?.as_str(), 8192)?;
    let html = parts
        .iter()
        .rev()
        .map(|s| unescape_literal(s))
        .collect::<Option<Vec<_>>>()?
        .concat();
    super::super::declared::player(base, &Document::from(html.as_str()))
}

pub(super) fn known(url: &Url) -> bool {
    matches_host(url, NUPLOAD)
        || matches_host(url, DOOD)
        || super::super::pixeldrain::address(url).is_some()
        || matches_host(
            url,
            &[
                "ironhentai.com",
                "speedfiles.net",
                "speedfiles.io",
                "loadx.ws",
                "loadx.top",
                "vixcloud.co",
                "frembed.click",
                "ok.ru",
                "odnoklassniki.ru",
                "mediafire.com",
                "player.zilla-networks.com",
                "mixdrop.co",
                "mixdrop.ag",
                "mixdrop.to",
                "mixdrop.is",
                "mixdrop.top",
                "miixdrop.top",
                "mxdrop.click",
            ],
        )
}

pub(in crate::streams) fn loadx_endpoint(page: &Url) -> Option<Url> {
    if !matches_host(page, &["loadx.ws", "loadx.top"]) {
        return None;
    }
    let id = page.path().trim_end_matches('/').rsplit('/').next()?;
    if id.is_empty()
        || id.len() > 128
        || !id
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_'))
    {
        return None;
    }
    let mut endpoint = Url::parse("https://loadx.ws/player/index.php").ok()?;
    endpoint
        .query_pairs_mut()
        .append_pair("data", id)
        .append_pair("do", "getVideo");
    Some(endpoint)
}

pub(in crate::streams) fn dood_request(base: &Url, body: &str) -> Option<(Url, String)> {
    if !matches_host(base, DOOD) && !base.path().starts_with("/e/") {
        return None;
    }
    let found = DOOD_PATH.captures(body)?;
    Some((base.join(&found[1]).ok()?, found[2].to_owned()))
}

pub(in crate::streams) fn dood_media(base: &Url, body: &str, token: &str) -> Option<Stream> {
    if body.len() > 4096 {
        return None;
    }
    let prefix = Url::parse(body.trim()).ok()?;
    if prefix.query().is_some() || prefix.fragment().is_some() {
        return None;
    }
    let suffix = uuid::Uuid::new_v4().simple().to_string();
    let mut url = Url::parse(&format!("{}{}", prefix, &suffix[..10])).ok()?;
    url.query_pairs_mut()
        .append_pair("token", token)
        .append_pair("expiry", &chrono::Utc::now().timestamp_millis().to_string());
    declared::stream(base, url.as_str(), "video/mp4")
}

pub(in crate::streams) fn frembed(base: &Url, body: &str) -> Option<Url> {
    if !matches_host(base, &["frembed.click"]) {
        return None;
    }
    let value: Value = serde_json::from_str(body).ok()?;
    let candidates: Vec<_> = value
        .as_object()?
        .iter()
        .filter_map(|(key, target)| {
            let number = key
                .to_ascii_lowercase()
                .strip_prefix("link")?
                .parse::<u32>()
                .ok()?;
            Some((
                number,
                declared::address(base, target.as_str()?, "subdocument")?,
            ))
        })
        .collect();
    candidates
        .into_iter()
        .min_by_key(|(number, url)| (!super::known(url), *number))
        .map(|(_, url)| url)
}
