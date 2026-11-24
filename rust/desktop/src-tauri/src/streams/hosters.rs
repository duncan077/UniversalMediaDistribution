use super::{declared, matches_host, Stream};
use base64::{engine::general_purpose::STANDARD, Engine};
use dom_query::Document;
use regex::Regex;
use reqwest::Url;
use serde_json::Value;
use std::sync::LazyLock;

pub(super) mod protocols;

pub(super) const BROWSER_USER_AGENT: &str = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

const VOE: &[&str] = &[
    "voe.sx",
    "jessicachoosemake.com",
    "matthewhotelscience.com",
    "jilliandescribecompany.com",
    "mikaylaarealike.com",
    "christopheruntilpoint.com",
    "walterprettytheir.com",
    "crystaltreatmenteast.com",
    "lauradaydo.com",
    "lancewhosedifficult.com",
    "dianaavoidthey.com",
    "jefferycontrolmodel.com",
    "charlestoughrace.com",
    "richardquestionbuilding.com",
    "jessicayeahcatch.com",
    "juliewomanwish.com",
    "caseyseemavoid.com",
];
const PACKED_HOSTS: &[&str] = &[
    "filemoon.sx",
    "filemoon.to",
    "filemoon.site",
    "bf0skv.org",
    "bysejikuar.com",
    "bysezoxexe.com",
    "bysebuho.com",
    "bysekoze.com",
    "bysesayeveum.com",
    "byselapuix.com",
    "moflix-stream.link",
    "streamwish.com",
    "streamwish.to",
    "sfastwish.com",
    "flaswish.com",
    "vidhide.com",
    "vidhidepro.com",
    "vidhideplus.com",
    "vidhidevip.com",
    "callistanise.com",
    "streamsb.net",
    "sbembed.com",
    "sbplay.org",
    "sbrapid.com",
    "sbvideo.net",
    "ssbstream.net",
    "streamsss.net",
    "watchsb.com",
    "streamlare.com",
    "ninjastream.to",
    "uch.to",
    "vidmoly.to",
    "vidmoly.me",
    "vidoza.net",
    "yourupload.com",
    "yucache.net",
    "uqload.cx",
    "uqload.is",
    "uqload.com",
    "upzone.cc",
    "upzone.to",
    "upzone.net",
    "upzone.link",
    "upzur.com",
    "mp4upload.com",
];
const TAPE: &[&str] = &["streamtape.com", "streamtape.to"];
const MAX_PACKED_SCRIPT: usize = 2 << 20;

pub(super) fn alternate_player(url: &Url) -> bool {
    url.scheme() == "https"
        && super::web_url(url)
        && (matches_host(
            url,
            &[
                "mega.nz",
                "mega.co.nz",
                "uns.bio",
                "upns.pro",
                "hgcloud.to",
                "filemoon.site",
                "filemoon.sx",
                "filemoon.to",
                "bf0skv.org",
                "bysejikuar.com",
                "bysezoxexe.com",
                "bysebuho.com",
                "bysekoze.com",
                "bysesayeveum.com",
                "byselapuix.com",
                "moflix-stream.link",
            ],
        ) || matches_host(url, VOE)
            || poseidon_player(url))
}

fn poseidon_player(url: &Url) -> bool {
    matches_host(url, &["poseidonhd2.co"])
        && url
            .host_str()
            .is_some_and(|host| host.starts_with("player."))
}

pub(super) fn approved_delegate(page: &Url, next: &Url) -> bool {
    (poseidon_player(page) && alternate_player(next))
        || (matches_host(page, VOE) && matches_host(next, VOE) && alternate_player(next))
}

pub(super) fn mega_embed(url: &Url) -> Option<Url> {
    if url.scheme() != "https"
        || !super::web_url(url)
        || !matches_host(url, &["mega.nz", "mega.co.nz"])
    {
        return None;
    }
    let fragment = url.fragment()?;
    let (id, key) = if let Some(legacy) = fragment.strip_prefix('!') {
        if url.path() != "/" {
            return None;
        }
        legacy.split_once('!')?
    } else {
        let (kind, id) = url.path().trim_matches('/').split_once('/')?;
        if !matches!(kind, "file" | "embed") {
            return None;
        }
        (id, fragment)
    };
    let valid = |part: &str| {
        part.bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_'))
    };
    if id.len() != 8 || key.len() != 43 || !valid(id) || !valid(key) {
        return None;
    }
    Url::parse(&format!("https://mega.nz/embed/{id}#{key}")).ok()
}

pub(super) fn upnshare_endpoint(url: &Url) -> Option<Url> {
    if url.scheme() != "https"
        || url.host_str() != Some("animeav1.uns.bio")
        || url.path() != "/"
        || !super::web_url(url)
    {
        return None;
    }
    let id = url.fragment()?.trim();
    if id.is_empty()
        || id.len() > 128
        || !id
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.'))
    {
        return None;
    }
    let mut endpoint = url.join("/api/public/resolve").ok()?;
    endpoint.query_pairs_mut().append_pair("file1", id);
    Some(endpoint)
}

pub(super) fn known(url: &Url) -> bool {
    matches_host(url, PACKED_HOSTS)
        || matches_host(url, VOE)
        || matches_host(url, TAPE)
        || matches_host(url, &["hgcloud.to", "uns.bio", "upns.pro"])
        || protocols::known(url)
}

static COMMENTS: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?s)/\*.*?\*/").unwrap());
static BINDING: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?is)\b(?:file|src)\b["']?\s*:\s*["']([^"'<>]+)["']|\b(?:sources|playlist)\b["']?\s*[:=]\s*\[\s*["']([^"'<>]+)["']"#).unwrap()
});
static VIDEO_JS: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?is)\bplayer\.src\s*\(\s*(?:\{([^{}]{1,1024})\}|["']([^"'<>]+)["'])"#).unwrap()
});
static VIDEO_TYPE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?i)\btype\b["']?\s*:\s*["']([a-z0-9.+-]+/[a-z0-9.+-]+)["']"#).unwrap()
});
static DPLAYER: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?is)\bvideo\s*:\s*\{[^{}]{0,384}?\burl\b["']?\s*:\s*(?:(atob)\(\s*)?["']([^'"<>]+)["']"#).unwrap()
});
static MIXDROP: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r#"(?i)\b(?:MDCore\s*\.\s*wurl|MDCore\s*\[\s*["']wurl["']\s*\])\s*=\s*["']([^"'<>]+)["']"#,
    )
    .unwrap()
});
static ROBOT: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?s)["']robotlink["']\s*\)\s*\.innerHTML\s*=\s*["']([^"']*)["']\s*\+\s*\(\s*["']([^"']*)["']\s*\)((?:\s*\.substring\(\s*\d{1,6}\s*\))*)"#).unwrap()
});
static SUBSTRING: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"\.substring\(\s*(\d{1,6})\s*\)").unwrap());
static HLS: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?i)(?:\bhls\b["']?\s*:|\bprompt\(\s*["']Node["']\s*,)\s*["']([^"'<>]+)["']"#)
        .unwrap()
});
static REDIRECT: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?i)(?:(?:var|let|const)\s+url|(?:window\s*\.\s*)?location(?:\s*\.\s*href)?)\s*=\s*["']([^"'<>]+)["']|(?:window\s*\.\s*)?location\s*\.\s*replace\s*\(\s*["']([^"'<>]+)["']"#).unwrap()
});
static PACKER: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?s)\beval\s*\(\s*function\s*\(\s*p\s*,\s*a\s*,\s*c\s*,\s*k\s*,\s*e\s*,\s*[dr]\s*\).*?\}\s*\(\s*(?:"((?:\\.|[^"\\])*)"|'((?:\\.|[^'\\])*)')\s*,\s*(\d{1,2})\s*,\s*(\d{1,6})\s*,\s*(?:"((?:\\.|[^"\\])*)"|'((?:\\.|[^'\\])*)')\s*\.split\(\s*["']\|["']\s*\)"#).unwrap()
});
static WORD: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"[a-zA-Z0-9_]+").unwrap());

fn scripts(doc: &Document) -> impl Iterator<Item = String> + '_ {
    doc.select("script:not([src])")
        .iter()
        .take(128)
        .map(|el| el.text().to_string())
        .collect::<Vec<_>>()
        .into_iter()
}

fn unescape_literal(value: &str) -> Option<String> {
    let mut result = String::with_capacity(value.len());
    let mut chars = value.chars();
    while let Some(ch) = chars.next() {
        if ch != '\\' {
            result.push(ch);
            continue;
        }
        match chars.next()? {
            '\\' => result.push('\\'),
            ch @ ('/' | '"' | '\'') => result.push(ch),
            'n' => result.push('\n'),
            'r' => result.push('\r'),
            't' => result.push('\t'),
            '\n' => {}
            kind @ ('u' | 'x') => {
                let count = if kind == 'u' { 4 } else { 2 };
                let code = (0..count)
                    .try_fold(0, |code, _| Some(code * 16 + chars.next()?.to_digit(16)?))?;
                result.push(char::from_u32(code)?);
            }
            other => {
                result.push('\\');
                result.push(other);
            }
        }
    }
    Some(result)
}

// Dean Edwards' dictionary format. Treat the script as data and never evaluate it.
fn unpack(script: &str) -> Option<String> {
    if script.len() > MAX_PACKED_SCRIPT {
        return None;
    }
    let caps = PACKER.captures(script)?;
    let radix: usize = caps[3].parse().ok()?;
    let count: usize = caps[4].parse().ok()?;
    if !(2..=62).contains(&radix) || count > 100_000 {
        return None;
    }
    let payload = unescape_literal(caps.get(1).or_else(|| caps.get(2))?.as_str())?;
    let words = unescape_literal(caps.get(5).or_else(|| caps.get(6))?.as_str())?;
    let dictionary: Vec<_> = words.split('|').take(count + 1).collect();
    if dictionary.len() > count {
        return None;
    }
    let mut result = String::with_capacity(payload.len());
    let mut cursor = 0;
    for token in WORD.find_iter(&payload) {
        let word = token.as_str();
        let index = word.bytes().try_fold(0usize, |index, c| {
            let digit = b"0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"
                .iter()
                .position(|d| *d == c)?;
            if digit >= radix {
                return None;
            }
            index.checked_mul(radix)?.checked_add(digit)
        });
        let replacement = index
            .and_then(|i| dictionary.get(i))
            .copied()
            .filter(|v| !v.is_empty())
            .unwrap_or(word);
        result.push_str(&payload[cursor..token.start()]);
        result.push_str(replacement);
        if result.len() > 4 * MAX_PACKED_SCRIPT {
            return None;
        }
        cursor = token.end();
    }
    result.push_str(&payload[cursor..]);
    (result.len() <= 4 * MAX_PACKED_SCRIPT).then_some(result)
}

fn decoded_base64(value: &str) -> Option<String> {
    let decoded = STANDARD.decode(value.trim()).ok().or_else(|| {
        base64::engine::general_purpose::STANDARD_NO_PAD
            .decode(value.trim())
            .ok()
    })?;
    String::from_utf8(decoded).ok()
}

fn voe_payload(encoded: &str) -> Option<String> {
    let mut rotated: String = encoded
        .chars()
        .map(|c| match c {
            'a'..='z' => (b'a' + (c as u8 - b'a' + 13) % 26) as char,
            'A'..='Z' => (b'A' + (c as u8 - b'A' + 13) % 26) as char,
            _ => c,
        })
        .collect();
    for junk in ["@$", "^^", "~@", "%?", "*~", "!!", "#&"] {
        rotated = rotated.replace(junk, "");
    }
    let shifted = decoded_base64(&rotated)?;
    let reversed: Vec<_> = shifted
        .bytes()
        .rev()
        .map(|b| b.checked_sub(3))
        .collect::<Option<_>>()?;
    let json = decoded_base64(std::str::from_utf8(&reversed).ok()?)?;
    serde_json::from_str::<Value>(&json).ok()?["source"]
        .as_str()
        .map(str::to_owned)
}

fn tape_stream(base: &Url, doc: &Document) -> Option<Stream> {
    let to_stream = |value: &str| {
        let url = declared::address(base, value, "media")?;
        (url.path().ends_with("/get_video") || url.path().ends_with(".mp4"))
            .then(|| super::media(url, "video/mp4"))
            .flatten()
    };
    for script in scripts(doc) {
        for link in ROBOT.captures_iter(&script) {
            let offset = SUBSTRING
                .captures_iter(&link[3])
                .try_fold(0usize, |sum, offset| {
                    sum.checked_add(offset[1].parse().ok()?)
                });
            if let Some(stream) = offset
                .and_then(|offset| link[2].get(offset..))
                .and_then(|suffix| to_stream(&format!("{}{suffix}", &link[1])))
            {
                return Some(stream);
            }
        }
    }
    doc.select("#robotlink,#ideoooolink,a#download-video[href]")
        .iter()
        .find_map(|el| to_stream(&el.attr("href").unwrap_or_else(|| el.text())))
}

pub(super) fn extract(base: &Url, doc: &Document) -> Option<Stream> {
    if matches_host(base, TAPE) {
        if let Some(stream) = tape_stream(base, doc) {
            return Some(stream);
        }
    }
    let voe = matches_host(base, VOE);
    for el in doc
        .select(r#"script[type="application/json"]"#)
        .iter()
        .take(32)
    {
        let Ok(value) = serde_json::from_str::<Value>(&el.text()) else {
            continue;
        };
        let encoded = value.as_str().or_else(|| value[0].as_str());
        if voe || (base.scheme() == "https" && base.path().starts_with("/e/")) {
            if let Some(stream) = encoded
                .filter(|v| v.len() <= 1 << 20)
                .and_then(voe_payload)
                .and_then(|v| declared::stream(base, &v, "application/vnd.apple.mpegurl"))
            {
                return Some(stream);
            }
        }
    }
    let parse = |raw: &str, mime: &str| declared::stream(base, &unescape_literal(raw)?, mime);
    for script in scripts(doc) {
        let cleaned = COMMENTS.replace_all(&script, "");
        if let Some(stream) = protocols::extract(base, &cleaned) {
            return Some(stream);
        }
        let unpacked = unpack(&cleaned);
        for code in std::iter::once(cleaned.as_ref()).chain(unpacked.as_deref()) {
            if let Some(stream) = MIXDROP
                .captures_iter(code)
                .find_map(|c| parse(&c[1], "video/mp4"))
                .or_else(|| {
                    VIDEO_JS.captures_iter(code).find_map(|c| {
                        if let Some(address) = c.get(2) {
                            return parse(address.as_str(), "");
                        }
                        let object = c.get(1)?.as_str();
                        let field = BINDING.captures(object)?;
                        let mime = VIDEO_TYPE.captures(object).and_then(|t| t.get(1));
                        parse(
                            field.get(1)?.as_str(),
                            mime.map_or("", |value| value.as_str()),
                        )
                    })
                })
            {
                return Some(stream);
            }
            if code.contains("new DPlayer") {
                for c in DPLAYER.captures_iter(code).filter(|c| c[2].len() <= 16_384) {
                    let found = if c.get(1).is_some() {
                        decoded_base64(&c[2]).and_then(|v| declared::stream(base, &v, ""))
                    } else {
                        parse(&c[2], "")
                    };
                    if found.is_some() {
                        return found;
                    }
                }
            }
            if voe || code.contains("sources") || code.contains("prompt(") {
                if let Some(stream) = HLS.captures_iter(code).find_map(|c| {
                    let raw = unescape_literal(&c[1])?;
                    declared::stream(base, &raw, "").or_else(|| {
                        let decoded = decoded_base64(&raw)?;
                        let url = Url::parse(&decoded).ok()?;
                        (url.scheme() == "https").then_some(())?;
                        declared::stream(base, &decoded, "application/vnd.apple.mpegurl")
                    })
                }) {
                    return Some(stream);
                }
            }
            if matches_host(base, PACKED_HOSTS)
                || matches_host(base, protocols::NUPLOAD)
                || code.contains("jwplayer(")
                || code.contains(".setup(")
                || unpacked.is_some() && code.contains("sources")
            {
                if let Some(stream) = BINDING
                    .captures_iter(code)
                    .find_map(|c| parse(c.get(1).or_else(|| c.get(2))?.as_str(), ""))
                {
                    return Some(stream);
                }
            }
        }
    }
    None
}

pub(super) fn redirect(base: &Url, doc: &Document) -> Option<Url> {
    let voe = matches_host(base, VOE);
    let wrapper = (matches_host(base, &["tioplus.app", "pelisplus.to"])
        && base.path().starts_with("/player/"))
        || poseidon_player(base)
        || matches_host(base, PACKED_HOSTS)
        || matches_host(base, protocols::NUPLOAD);
    if !voe && !wrapper {
        return None;
    }
    for script in scripts(doc) {
        let cleaned = COMMENTS.replace_all(&script, "");
        for c in REDIRECT.captures_iter(&cleaned) {
            let raw = unescape_literal(c.get(1).or_else(|| c.get(2))?.as_str())?;
            let Some(url) = declared::address(base, &raw, "subdocument") else {
                continue;
            };
            if url.scheme() == "https"
                && (!voe || matches_host(&url, VOE) || url.path() == base.path())
            {
                return Some(url);
            }
        }
    }
    None
}
