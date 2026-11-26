use super::*;
use dom_query::Document;
use regex::Regex;
use std::{collections::BTreeMap, sync::LazyLock};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Manga {
    Leer,
    Zona,
    Uchuujin,
}

static CHAPTER: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)(?:cap[ií]tulo|chapter|cap\.?)\s*[:#-]?\s*(\d+(?:[.,]\d+)?)").unwrap()
});
static NUMBER: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\d+(?:[.,]\d+)?").unwrap());
static NON_DIGITS: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"[^0-9]+").unwrap());
static LEER_BOOTSTRAP: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r"(?s)function\s+([$A-Za-z_][$A-Za-z0-9_]*)\s*\([^)]*\)\s*\{\s*var\s+[$A-Za-z_][$A-Za-z0-9_]*\s*=\s*([$A-Za-z_][$A-Za-z0-9_]*)\s*\(\s*\)\s*;\s*return\s+([$A-Za-z_][$A-Za-z0-9_]*)\s*=\s*function\s*\(\s*([$A-Za-z_][$A-Za-z0-9_]*)\s*,[^)]*\)\s*\{\s*([$A-Za-z_][$A-Za-z0-9_]*)\s*=\s*([$A-Za-z_][$A-Za-z0-9_]*)\s*-\s*([^;]+);",
    )
    .unwrap()
});

fn content(doc: &Document, s: &str) -> String {
    doc.select(s)
        .first()
        .text()
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
}

fn first_img_attr(node: &dom_query::Selection<'_>) -> Option<String> {
    let img = node.select("img").first();
    img.attr("data-src")
        .or_else(|| img.attr("src"))
        .map(|s| s.to_string())
}

fn book_categories(subtype: Option<&str>, label: Option<&str>) -> Vec<String> {
    let mut categories = Vec::new();
    if subtype == Some("manga") {
        categories.push("Manga".into());
    }
    if let Some(label) =
        label.filter(|label| !label.is_empty() && !label.eq_ignore_ascii_case("manga"))
    {
        categories.push(label.to_owned());
    }
    categories
}

fn preferred_type_label<'a>(label: Option<&'a str>, route: Option<&'a str>) -> Option<&'a str> {
    let kind = |label: &str| umd_core::provider_book_subtype(label);
    let label = label.filter(|label| kind(label).is_some());
    if label
        .and_then(kind)
        .is_some_and(|kind| matches!(kind, "manga" | "comic"))
        && route
            .and_then(kind)
            .is_some_and(|kind| !matches!(kind, "manga" | "comic"))
    {
        return route;
    }
    label.or(route)
}

impl Manga {
    pub fn base(self) -> &'static str {
        match self {
            Self::Leer => "https://www.leercapitulo.co/",
            Self::Zona => "https://zonatmo.org/",
            Self::Uchuujin => "https://uchuujinmangas.com/",
        }
    }
    fn id(self, path: &str) -> Option<&str> {
        let id = path.trim_matches('/').strip_prefix(if self == Self::Zona {
            "library/"
        } else {
            "manga/"
        })?;
        let parts: Vec<_> = id.split('/').collect();
        if !parts.iter().all(|s| segment(s, 300)) {
            return None;
        }
        let valid = match (self, parts.as_slice()) {
            (Self::Zona, [_, n, _]) => n.parse::<u64>().is_ok(),
            (Self::Leer, [_, _]) => true,
            (Self::Uchuujin, [slug]) => !matches!(*slug, "list-mode" | "page"),
            _ => false,
        };
        valid.then_some(id)
    }
    fn path(self, id: &str) -> Result<String> {
        let path = format!(
            "{}/{id}/",
            if self == Self::Zona {
                "library"
            } else {
                "manga"
            }
        );
        if self.id(&path) != Some(id) {
            return Err(invalid());
        }
        Ok(path)
    }
    fn type_label<'a>(self, id: &'a str) -> Option<&'a str> {
        match self {
            Self::Zona => id.split_once('/').map(|(kind, _)| kind),
            Self::Leer => Some("Manga"),
            Self::Uchuujin => None,
        }
    }

    fn entry(self, id: &str, title: &str, thumb: Option<String>, label: Option<&str>) -> Entry {
        let label = preferred_type_label(label, self.type_label(id));
        let subtype = label.and_then(umd_core::provider_book_subtype);
        Entry {
            external_id: id.to_owned(),
            preview: json!({
                "name": title,
                "mediaType": "book",
                "subtype": subtype,
                "thumbnailRef": thumb,
                "categories": book_categories(subtype, label)
            }),
        }
    }
    fn titles(self, base: &Url, doc: &Document) -> Vec<Entry> {
        if self == Self::Leer {
            let cards = dedup(doc.select("article.lc-card").iter().filter_map(|card| {
                let title = card.select("a.lc-card-name[href]").first();
                let url = base.join(&title.attr("href")?).ok()?;
                if url.origin() != base.origin() {
                    return None;
                }
                let id = self.id(url.path())?;
                let name = title.text();
                let thumb = artwork_url(base, first_img_attr(&card).as_deref());
                let label = card.select(".lc-card-badge").first().text();
                (!name.trim().is_empty())
                    .then(|| self.entry(id, name.trim(), thumb, Some(label.trim())))
            }));
            if !cards.is_empty() {
                return cards;
            }
        }
        dedup(doc.select("a[href]").iter().filter_map(|a| {
            let url = base.join(&a.attr("href")?).ok()?;
            if url.origin() != base.origin() {
                return None;
            }
            let id = self.id(url.path())?;
            let heading = a.select("h2,h3,h4,h5,.tt");
            let name = [
                heading.attr("title").map(|s| s.to_string()),
                Some(heading.first().text().to_string()),
                a.attr("title").map(|s| s.to_string()),
                a.select("img").attr("alt").map(|s| s.to_string()),
                Some(a.text().to_string()),
            ]
            .into_iter()
            .flatten()
            .find(|s| !s.trim().is_empty())?;
            let thumb = artwork_url(base, first_img_attr(&a).as_deref());
            let label = a.select(".book-type,.type,.typez").first().text();
            let label = label.trim();
            Some(self.entry(
                id,
                &name.split_whitespace().collect::<Vec<_>>().join(" "),
                thumb,
                Some(label),
            ))
        }))
    }

    fn detail_type(
        self,
        doc: &Document,
        id: &str,
        preview: &Value,
    ) -> (&'static str, Option<String>) {
        let label = match self {
            Self::Zona => content(doc, "h1.book-type,.book-type"),
            Self::Uchuujin => doc
                .select("div.imptdt")
                .iter()
                .find_map(|node| {
                    let text = node.text();
                    let start = text.trim();
                    let prefix = start.get(..4)?;
                    (prefix.eq_ignore_ascii_case("tipo") || prefix.eq_ignore_ascii_case("type"))
                        .then(|| {
                            start[4..]
                                .trim_matches(|c: char| c.is_whitespace() || c == ':')
                                .to_owned()
                        })
                })
                .unwrap_or_default(),
            Self::Leer => String::new(),
        };
        if let Some(label) = preferred_type_label(Some(&label), self.type_label(id)) {
            if let Some(subtype) = umd_core::provider_book_subtype(label) {
                return (subtype, Some(label.to_owned()));
            }
        }
        let subtype = preview["subtype"]
            .as_str()
            .and_then(umd_core::provider_book_subtype)
            .unwrap_or("manga");
        (subtype, None)
    }
    fn chapter_links(
        self,
        base: &Url,
        id: &str,
        doc: &Document,
    ) -> Result<Vec<(String, String, Url)>> {
        let mut entries = BTreeMap::new();
        let prefix = format!("/leer/{id}/");
        let selector = match self {
            Self::Zona => "li.upload-link[data-chapter-number]",
            Self::Leer => "a[href]",
            Self::Uchuujin => "#chapterlist li, li[data-num], .eph-num",
        };
        for node in doc.select(selector).iter() {
            let a = if self == Self::Leer {
                node.clone()
            } else {
                node.select("a[href]")
            };
            for link in a.iter() {
                let Some(u) = link
                    .attr("href")
                    .and_then(|s| base.join(&s).ok())
                    .filter(|u| u.origin() == base.origin())
                else {
                    continue;
                };
                let path = u.path().trim_end_matches('/');
                let label = link
                    .attr("title")
                    .map(|s| s.to_string())
                    .unwrap_or_else(|| link.text().to_string());
                let n = match self {
                    Self::Zona
                        if path
                            .strip_prefix("/view_uploads/")
                            .and_then(|n| n.parse::<u64>().ok())
                            .is_some() =>
                    {
                        node.attr("data-chapter-number").map(|v| v.to_string())
                    }
                    Self::Leer if path.strip_prefix(&prefix).is_some_and(|r| !r.contains('/')) => {
                        CHAPTER
                            .captures(&label)
                            .map(|c| c[1].to_string())
                            .or_else(|| {
                                NUMBER
                                    .find(&path[prefix.len()..])
                                    .map(|m| m.as_str().into())
                            })
                    }
                    Self::Uchuujin if path.trim_matches('/').split('/').count() == 4 => node
                        .attr("data-num")
                        .map(|s| s.to_string())
                        .or_else(|| CHAPTER.captures(&label).map(|c| c[1].to_owned()))
                        .or_else(|| {
                            NUMBER
                                .find(&link.select(".chapternum").text())
                                .map(|m| m.as_str().to_owned())
                        }),
                    _ => None,
                };
                if let Some(n) = n.filter(|s| !s.trim().is_empty() && s.len() < 100) {
                    let key = canonical_number(&n);
                    if !key.is_empty() {
                        entries
                            .entry(key)
                            .or_insert((n.replace(',', "."), label, u));
                    }
                }
                if entries.len() > 20000 {
                    return Err(invalid());
                }
            }
        }
        let mut out: Vec<_> = entries.into_values().collect();
        out.sort_by(|a, b| {
            let num = |s: &str| {
                s.parse::<f64>()
                    .ok()
                    .filter(|v| v.is_finite())
                    .unwrap_or(f64::INFINITY)
            };
            num(&a.0).total_cmp(&num(&b.0)).then_with(|| a.0.cmp(&b.0))
        });
        Ok(out)
    }
}

fn find_leer_reader_scripts(base: &Url, doc: &Document) -> Vec<Url> {
    let mut preferred = Vec::new();
    let mut fallback = Vec::new();
    let mut seen = HashSet::new();
    for s in doc.select("script[src]").iter() {
        let Some(src) = s.attr("src") else {
            continue;
        };
        let Some(u) = base.join(&src).ok().filter(|u| {
            u.origin() == base.origin() && u.path().to_ascii_lowercase().ends_with(".js")
        }) else {
            continue;
        };
        if !seen.insert(u.clone()) {
            continue;
        }
        if u.path().to_ascii_lowercase().ends_with("/pvatvp.js") {
            preferred.push(u);
        } else {
            fallback.push(u);
        }
    }
    preferred.extend(fallback);
    preferred
}

fn skip_ws(bytes: &[u8], pos: &mut usize) {
    while *pos < bytes.len() && bytes[*pos].is_ascii_whitespace() {
        *pos += 1;
    }
}

fn is_ident_start(b: u8) -> bool {
    b.is_ascii_alphabetic() || matches!(b, b'_' | b'$')
}

fn is_ident_part(b: u8) -> bool {
    is_ident_start(b) || b.is_ascii_digit()
}

fn read_hex_u32(s: &str, pos: &mut usize, count: usize) -> Option<u32> {
    let end = pos.checked_add(count)?;
    let slice = s.get(*pos..end)?;
    let val = u32::from_str_radix(slice, 16).ok()?;
    *pos = end;
    Some(val)
}

fn read_js_string(s: &str, pos: &mut usize) -> Option<String> {
    let bytes = s.as_bytes();
    let quote = *bytes.get(*pos)?;
    if quote != b'\'' && quote != b'"' {
        return None;
    }
    *pos += 1;
    let mut out = String::new();
    while *pos < bytes.len() {
        let b = bytes[*pos];
        if b == quote {
            *pos += 1;
            return Some(out);
        }
        if b != b'\\' {
            let ch = s[*pos..].chars().next()?;
            out.push(ch);
            *pos += ch.len_utf8();
            continue;
        }
        *pos += 1;
        let esc = *bytes.get(*pos)?;
        *pos += 1;
        match esc {
            b'\r' => {
                if bytes.get(*pos) == Some(&b'\n') {
                    *pos += 1;
                }
            }
            b'\n' => {}
            b'b' => out.push('\u{0008}'),
            b'f' => out.push('\u{000C}'),
            b'n' => out.push('\n'),
            b'r' => out.push('\r'),
            b't' => out.push('\t'),
            b'v' => out.push('\u{000B}'),
            b'0' => out.push('\0'),
            b'x' => {
                let cp = read_hex_u32(s, pos, 2)?;
                out.push(char::from_u32(cp)?);
            }
            b'u' => {
                if bytes.get(*pos) == Some(&b'{') {
                    *pos += 1;
                    let rel_end = s[*pos..].find('}')?;
                    let hex = &s[*pos..*pos + rel_end];
                    let cp = u32::from_str_radix(hex, 16).ok()?;
                    out.push(char::from_u32(cp)?);
                    *pos += rel_end + 1;
                } else {
                    let cp = read_hex_u32(s, pos, 4)?;
                    out.push(char::from_u32(cp)?);
                }
            }
            other => out.push(other as char),
        }
    }
    None
}

fn skip_js_string(bytes: &[u8], pos: &mut usize, quote: u8) -> bool {
    *pos += 1;
    while *pos < bytes.len() {
        if bytes[*pos] == b'\\' {
            *pos += 2;
            continue;
        }
        if bytes[*pos] == quote {
            return true;
        }
        *pos += 1;
    }
    false
}

fn find_matching_paren(s: &str, open_pos: usize) -> Option<usize> {
    let bytes = s.as_bytes();
    let mut depth = 0usize;
    let mut pos = open_pos;
    while pos < bytes.len() {
        let b = bytes[pos];
        if matches!(b, b'\'' | b'"' | b'`') {
            if !skip_js_string(bytes, &mut pos, b) {
                return None;
            }
            pos += 1;
            continue;
        }
        if b == b'/' && pos + 1 < bytes.len() {
            if bytes[pos + 1] == b'/' {
                pos += 2;
                while pos < bytes.len() && !matches!(bytes[pos], b'\r' | b'\n') {
                    pos += 1;
                }
                continue;
            }
            if bytes[pos + 1] == b'*' {
                let end = s[pos + 2..].find("*/")?;
                pos = pos + 2 + end + 2;
                continue;
            }
        }
        if b == b'(' {
            depth += 1;
        } else if b == b')' {
            depth = depth.checked_sub(1)?;
            if depth == 0 {
                return Some(pos);
            }
        }
        pos += 1;
    }
    None
}

struct IntExpr<'a> {
    bytes: &'a [u8],
    pos: usize,
    depth: usize,
}

impl<'a> IntExpr<'a> {
    fn eval(expr: &'a str) -> Option<i64> {
        let mut p = Self {
            bytes: expr.as_bytes(),
            pos: 0,
            depth: 0,
        };
        let v = p.add()?;
        skip_ws(p.bytes, &mut p.pos);
        (p.pos == p.bytes.len()).then_some(v)
    }
    fn consume(&mut self, ch: u8) -> bool {
        if self.bytes.get(self.pos) == Some(&ch) {
            self.pos += 1;
            true
        } else {
            false
        }
    }
    fn add(&mut self) -> Option<i64> {
        let mut left = self.mul()?;
        loop {
            skip_ws(self.bytes, &mut self.pos);
            if self.consume(b'+') {
                left = left.checked_add(self.mul()?)?;
            } else if self.consume(b'-') {
                left = left.checked_sub(self.mul()?)?;
            } else {
                return Some(left);
            }
        }
    }
    fn mul(&mut self) -> Option<i64> {
        let mut left = self.unary()?;
        loop {
            skip_ws(self.bytes, &mut self.pos);
            if self.consume(b'*') {
                left = left.checked_mul(self.unary()?)?;
            } else if self.consume(b'/') {
                let r = self.unary()?;
                left = left.checked_div(r)?;
            } else if self.consume(b'%') {
                let r = self.unary()?;
                left = left.checked_rem(r)?;
            } else {
                return Some(left);
            }
        }
    }
    fn unary(&mut self) -> Option<i64> {
        self.depth += 1;
        if self.depth > 64 {
            return None;
        }
        skip_ws(self.bytes, &mut self.pos);
        let res = if self.consume(b'+') {
            self.unary()
        } else if self.consume(b'-') {
            self.unary()?.checked_neg()
        } else if self.consume(b'(') {
            let v = self.add()?;
            skip_ws(self.bytes, &mut self.pos);
            self.consume(b')').then_some(v)
        } else {
            self.number()
        };
        self.depth -= 1;
        res
    }
    fn number(&mut self) -> Option<i64> {
        skip_ws(self.bytes, &mut self.pos);
        if self.pos + 2 <= self.bytes.len()
            && self.bytes[self.pos] == b'0'
            && matches!(self.bytes[self.pos + 1], b'x' | b'X')
        {
            self.pos += 2;
            let start = self.pos;
            while self.pos < self.bytes.len() && self.bytes[self.pos].is_ascii_hexdigit() {
                self.pos += 1;
            }
            if self.pos == start {
                return None;
            }
            let hex = std::str::from_utf8(&self.bytes[start..self.pos]).ok()?;
            return i64::from_str_radix(hex, 16).ok();
        }
        let start = self.pos;
        while self.pos < self.bytes.len() && self.bytes[self.pos].is_ascii_digit() {
            self.pos += 1;
        }
        if self.pos == start {
            return None;
        }
        std::str::from_utf8(&self.bytes[start..self.pos])
            .ok()?
            .parse::<i64>()
            .ok()
    }
}

fn parse_string_table(script: &str, fn_name: &str) -> Option<Vec<String>> {
    static TABLE: LazyLock<Regex> = LazyLock::new(|| {
        Regex::new(
        r"function\s+([$A-Za-z_][$A-Za-z0-9_]*)\s*\(\s*\)\s*\{\s*var\s+[$A-Za-z_][$A-Za-z0-9_]*\s*=\s*\["
    ).unwrap()
    });
    let mut pos = TABLE
        .captures_iter(script)
        .find(|candidate| {
            candidate
                .get(1)
                .is_some_and(|name| name.as_str() == fn_name)
        })?
        .get(0)?
        .end();
    let bytes = script.as_bytes();
    let mut values = Vec::new();
    while pos < bytes.len() {
        skip_ws(bytes, &mut pos);
        if bytes.get(pos) == Some(&b']') {
            return Some(values);
        }
        values.push(read_js_string(script, &mut pos)?);
        skip_ws(bytes, &mut pos);
        if bytes.get(pos) == Some(&b',') {
            pos += 1;
            continue;
        }
        if bytes.get(pos) == Some(&b']') {
            return Some(values);
        }
        return None;
    }
    None
}

fn extract_alphabet_exprs(script: &str, start: usize) -> Option<(&str, &str)> {
    let arrow = script[start..].find("=>")?;
    if arrow > 256 {
        return None;
    }
    let bytes = script.as_bytes();
    let mut pos = start + arrow + 2;
    skip_ws(bytes, &mut pos);
    if bytes.get(pos) != Some(&b'(') {
        return None;
    }
    let decoded_end = find_matching_paren(script, pos)?;
    let decoded_expr = &script[pos + 1..decoded_end];
    pos = decoded_end + 1;
    skip_ws(bytes, &mut pos);
    if bytes.get(pos) != Some(&b'[') {
        return None;
    }
    pos += 1;
    skip_ws(bytes, &mut pos);
    if bytes.get(pos) != Some(&b'(') {
        return None;
    }
    let encoded_end = find_matching_paren(script, pos)?;
    let encoded_expr = &script[pos + 1..encoded_end];
    Some((decoded_expr, encoded_expr))
}

fn parse_decoder_index(raw: &str) -> Option<i64> {
    let trimmed = raw.trim();
    if let Some(hex) = trimmed
        .strip_prefix("0x")
        .or_else(|| trimmed.strip_prefix("0X"))
    {
        i64::from_str_radix(hex, 16).ok()
    } else {
        trimmed.parse::<i64>().ok()
    }
}

fn eval_concat(expr: &str, table: &[String], offset: i64, rotation: usize) -> Option<String> {
    let bytes = expr.as_bytes();
    let mut pos = 0;
    let mut needs_token = true;
    let mut out = String::with_capacity(80);
    while pos < bytes.len() {
        skip_ws(bytes, &mut pos);
        if pos >= bytes.len() {
            break;
        }
        if !needs_token {
            if bytes[pos] != b'+' {
                return None;
            }
            pos += 1;
            needs_token = true;
            continue;
        }
        if matches!(bytes[pos], b'\'' | b'"') {
            out.push_str(&read_js_string(expr, &mut pos)?);
        } else {
            if !is_ident_start(bytes[pos]) {
                return None;
            }
            pos += 1;
            while pos < bytes.len() && is_ident_part(bytes[pos]) {
                pos += 1;
            }
            skip_ws(bytes, &mut pos);
            if bytes.get(pos) != Some(&b'(') {
                return None;
            }
            pos += 1;
            skip_ws(bytes, &mut pos);
            let raw_idx = if matches!(bytes.get(pos), Some(b'\'' | b'"')) {
                read_js_string(expr, &mut pos)?
            } else {
                let start = pos;
                while pos < bytes.len()
                    && !matches!(bytes[pos], b')' | b' ' | b'\t' | b'\r' | b'\n')
                {
                    pos += 1;
                }
                expr[start..pos].to_string()
            };
            skip_ws(bytes, &mut pos);
            if bytes.get(pos) != Some(&b')') {
                return None;
            }
            pos += 1;
            let idx = parse_decoder_index(&raw_idx)?;
            let table_idx = usize::try_from(idx.checked_sub(offset)?).ok()?;
            if table_idx >= table.len() {
                return None;
            }
            out.push_str(&table[(table_idx + rotation) % table.len()]);
        }
        if out.len() > 256 {
            return None;
        }
        needs_token = false;
    }
    (!needs_token && !out.is_empty()).then_some(out)
}

fn is_ascii_alnum_perm(s: &str) -> bool {
    if s.len() != 62 {
        return false;
    }
    let mut seen = [false; 128];
    for b in s.bytes() {
        if !b.is_ascii_alphanumeric() || seen[b as usize] {
            return false;
        }
        seen[b as usize] = true;
    }
    true
}

pub(crate) fn parse_leer_decoder(script: &str) -> Option<(String, String)> {
    let cap = LEER_BOOTSTRAP
        .captures_iter(script)
        .find(|c| c[1] == c[3] && c[4] == c[5] && c[4] == c[6])?;
    let table = parse_string_table(script, &cap[2])?;
    if table.is_empty() {
        return None;
    }
    let offset = IntExpr::eval(&cap[7])?;
    const MARKER: &str = "/[A-Z0-9]/gi";
    let mut search = 0;
    while let Some(rel) = script[search..].find(MARKER) {
        search += rel + MARKER.len();
        let Some((dec_expr, enc_expr)) = extract_alphabet_exprs(script, search) else {
            continue;
        };
        for rotation in 0..table.len() {
            let Some(decoded) = eval_concat(dec_expr, &table, offset, rotation) else {
                continue;
            };
            let Some(encoded) = eval_concat(enc_expr, &table, offset, rotation) else {
                continue;
            };
            if is_ascii_alnum_perm(&decoded) && is_ascii_alnum_perm(&encoded) {
                return Some((decoded, encoded));
            }
        }
    }
    None
}

pub(crate) fn decode_leer_images(
    encoded_payload: &str,
    order_key: &str,
    decoded_alphabet: &str,
    encoded_alphabet: &str,
    base: &Url,
) -> Result<Vec<String>> {
    if !is_ascii_alnum_perm(decoded_alphabet) || !is_ascii_alnum_perm(encoded_alphabet) {
        return Err(invalid());
    }
    let dec_bytes = decoded_alphabet.as_bytes();
    let mut table = [0u8; 128];
    for (i, &b) in encoded_alphabet.as_bytes().iter().enumerate() {
        table[b as usize] = dec_bytes[i];
    }
    let translated: String = encoded_payload
        .trim()
        .chars()
        .map(|c| {
            if c.is_ascii() {
                let mapped = table[c as usize];
                if mapped != 0 {
                    mapped as char
                } else {
                    c
                }
            } else {
                c
            }
        })
        .collect();
    let csv = decode_base64_utf8(&translated).ok_or_else(invalid)?;
    let shuffled: Vec<&str> = csv.split(',').map(str::trim).collect();
    if shuffled.is_empty() || shuffled.len() > 2000 || shuffled.iter().any(|s| s.is_empty()) {
        return Err(invalid());
    }
    let normalized = NON_DIGITS.replace_all(order_key, "-");
    let reversed: String = normalized.chars().rev().collect();
    let order: Vec<&str> = reversed.split('-').collect();
    if order.len() != shuffled.len() {
        return Err(invalid());
    }
    let mut seen = vec![false; shuffled.len()];
    let mut result = Vec::with_capacity(shuffled.len());
    for token in order {
        let idx = token.parse::<usize>().map_err(|_| invalid())?;
        if idx >= shuffled.len() || seen[idx] {
            return Err(invalid());
        }
        seen[idx] = true;
        let url = base
            .join(shuffled[idx])
            .ok()
            .filter(|u| u.scheme() == "https" && umd_core::public_url::web_url(u))
            .ok_or_else(invalid)?;
        result.push(url.to_string());
    }
    Ok(result)
}

pub(super) fn zona_cascade_url(url: &Url) -> Option<Url> {
    let trimmed = url.path().trim_end_matches('/');
    let prefix = trimmed.strip_suffix("/paginated")?;
    let mut cascade = url.clone();
    cascade.set_path(&format!("{prefix}/cascade"));
    (cascade.origin() == url.origin()).then_some(cascade)
}

impl Provider {
    async fn manga_title_html(&mut self, site: Manga, id: &str) -> Result<String> {
        let path = site.path(id)?;
        let (html, final_url) = self.html_with_url(&path).await?;
        if site == Manga::Zona {
            let returned = site.id(final_url.path()).ok_or_else(invalid)?;
            // Slugs can change; the type and numeric work ID identify the edition.
            if !returned.split('/').take(2).eq(id.split('/').take(2)) {
                return Err(invalid());
            }
        }
        Ok(html)
    }

    pub(super) async fn manga_page(
        &mut self,
        site: Manga,
        page: u32,
        query: Option<&str>,
    ) -> Result<Page> {
        let q: String =
            url::form_urlencoded::byte_serialize(query.unwrap_or("").as_bytes()).collect();
        let route = match (site, query) {
            (Manga::Leer, Some(_)) => format!("manga/?page={page}&q={q}"),
            (Manga::Leer, None) => format!("manga/?page={page}"),
            (Manga::Zona, Some(_)) => format!("biblioteca?filter_by=title&title={q}&page={page}"),
            (Manga::Zona, None) => {
                format!("biblioteca?order_item=creation&order_dir=desc&page={page}")
            }
            (Manga::Uchuujin, Some(_)) => format!("?s={q}&paged={page}"),
            (Manga::Uchuujin, None) => format!("manga/?order=update&page={page}"),
        };
        match self.manga_directory(site, &route, page, query.is_some()).await {
            Err(error)
                if site == Manga::Leer
                    && query.is_some()
                    && page == 1
                    && error.status == Some(404) =>
            {
                let v = self
                    .json(&format!("search-autocomplete?term={q}"), 4)
                    .await?;
                let items = dedup(rows(&v)?.iter().filter_map(|v| {
                    let u = self.base.join(v["link"].as_str()?).ok()?;
                    if u.origin() != self.base.origin() {
                        return None;
                    }
                    let thumb = artwork_url(
                        &self.base,
                        v["thumbnail"].as_str().or_else(|| v["image"].as_str()),
                    );
                    Some(site.entry(
                        site.id(u.path())?,
                        v["label"].as_str().or(v["value"].as_str())?,
                        thumb,
                        None,
                    ))
                }));
                Ok(Page { items, next: None })
            }
            result => result,
        }
    }
    pub(super) async fn manga_directory(&mut self, site: Manga, route: &str, page: u32, filtered: bool) -> Result<Page> {
        let doc = Document::from(self.html(route).await?);
        let items = site.titles(&self.base, &doc);
        let next = doc
            .select("a[href]")
            .iter()
            .any(|a| {
                a.attr("href")
                    .and_then(|s| self.base.join(&s).ok())
                    .is_some_and(|u| {
                        u.origin() == self.base.origin()
                            && (a
                                .attr("rel")
                                .is_some_and(|s| s.split_whitespace().any(|v| v == "next"))
                                || u.query_pairs().any(|(k, v)| {
                                    matches!(k.as_ref(), "page" | "paged")
                                        && v.parse::<u32>() == Ok(page + 1)
                                })
                                || u.path()
                                    .trim_end_matches('/')
                                    .ends_with(&format!("/page/{}", page + 1)))
                    })
            })
            .then(|| (page + 1).to_string());
        if filtered {
            Ok(Page { items, next })
        } else {
            Page::directory(items, next)
        }
    }
    pub(super) async fn manga_updates(&mut self, site: Manga) -> Result<Vec<Entry>> {
        if site == Manga::Leer {
            let doc = Document::from(self.html("").await?);
            let mut entries = site.titles(&self.base, &doc);
            if entries.is_empty() {
                for a in doc.select("a[href]").iter() {
                    let Some(u) = a.attr("href").and_then(|s| self.base.join(&s).ok()) else {
                        continue;
                    };
                    let parts: Vec<_> = u.path().trim_matches('/').split('/').collect();
                    if let ["leer", id, slug, _] = parts.as_slice() {
                        let key = format!("{id}/{slug}");
                        let title = a.text().split_whitespace().collect::<Vec<_>>().join(" ");
                        if !title.is_empty()
                            && u.origin() == self.base.origin()
                            && site.path(&key).is_ok()
                        {
                            let thumb = artwork_url(&self.base, first_img_attr(&a).as_deref());
                            entries.push(site.entry(&key, &title, thumb, None));
                        }
                    }
                }
            }
            return Ok(dedup(entries).into_iter().take(24).collect());
        }
        Ok(self
            .manga_page(site, 1, None)
            .await?
            .items
            .into_iter()
            .take(24)
            .collect())
    }
    pub(super) async fn manga_detail(&mut self, site: Manga, e: &Entry) -> Result<Value> {
        let doc = Document::from(self.manga_title_html(site, &e.external_id).await?);
        let selector = match site {
            Manga::Leer => "h1.title-manga,article.lc-panel h1",
            Manga::Zona => "h1.element-title,h1",
            Manga::Uchuujin => "h1.entry-title",
        };
        let title = content(&doc, selector);
        if title.is_empty() {
            return Err(invalid());
        }
        let (subtype, type_label) = site.detail_type(&doc, &e.external_id, &e.preview);
        let desc = content(
            &doc,
            ".manga-collapse,#sinopsis p,#manga-synopsis,.element-description,.entry-content",
        );
        let status = content(&doc, ".book-status,a[href*='status=']");
        let raw_thumb = doc
            .select("meta[property='og:image']")
            .attr("content")
            .or_else(|| {
                let img = doc
                    .select(".book-thumbnail img,.thumb img,article.lc-panel img,.cover img,.poster img")
                    .first();
                img.attr("data-src").or_else(|| img.attr("src"))
            })
            .map(|s| s.to_string());
        let thumb = artwork_url(&self.base, raw_thumb.as_deref())
            .or_else(|| artwork_url(&self.base, e.preview["thumbnailRef"].as_str()));
        let genre_links = "a[href*='genre='],a[href*='genre/'],a[href*='genero/'],.mgen a,.genres a,a[href*='genders']";
        let title_panel = doc.select("article.lc-panel").first();
        let genre_links = if site == Manga::Leer && !title_panel.is_empty() {
            title_panel.select(genre_links)
        } else {
            doc.select(genre_links)
        };
        let mut seen = HashSet::new();
        let genres: Vec<_> = genre_links
            .iter()
            .map(|a| a.text().trim().to_owned())
            .filter(|s| !s.is_empty() && seen.insert(s.clone()))
            .collect();
        let unit_links = site.chapter_links(&self.base, &e.external_id, &doc)?;
        let units: Vec<_> = unit_links
            .into_iter()
            .enumerate()
            .map(|(i, (n, label, _))| {
                json!({"kind":"chapter","displayNumber":n,"name":label.trim(),"sortOrder":i})
            })
            .collect();
        Ok(json!({
            "mediaType": "book",
            "subtype": subtype,
            "name": title,
            "description": desc,
            "thumbnailRef": thumb,
            "genres": genres,
            "publicationStatus": status,
            "categories": book_categories(Some(subtype), type_label.as_deref()),
            "units": units
        }))
    }
    pub async fn manga_images(
        &mut self,
        site: Manga,
        id: &str,
        chapter: &str,
    ) -> Result<Vec<String>> {
        if self.site != Site::Manga(site) {
            return Err(invalid());
        }
        let target = canonical_number(chapter);
        if target.is_empty() {
            return Err(invalid());
        }
        let route = {
            let doc = Document::from(self.manga_title_html(site, id).await?);
            let (subtype, _) = site.detail_type(&doc, id, &Value::Null);
            if !matches!(subtype, "manga" | "comic") {
                return Err(Failure::new("unsupported"));
            }
            site.chapter_links(&self.base, id, &doc)?
                .into_iter()
                .find(|(n, _, _)| canonical_number(n) == target)
                .ok_or_else(invalid)?
                .2
        };
        let (mut html, mut final_url) = self.html_with_url(route.as_str()).await?;
        if site == Manga::Zona {
            if let Some(cascade_url) = zona_cascade_url(&final_url) {
                let (cascade_html, cascade_final) =
                    self.html_with_url(cascade_url.as_str()).await?;
                html = cascade_html;
                final_url = cascade_final;
            }
        }
        let image = |s: &str| {
            final_url
                .join(s)
                .ok()
                .filter(|u| u.scheme() == "https" && umd_core::public_url::web_url(u))
                .map(String::from)
                .ok_or_else(invalid)
        };
        if site == Manga::Uchuujin {
            let doc = Document::from(html.as_str());
            for script in doc.select("script:not([src])").iter() {
                let s = script.text();
                let Some((_, v)) = s.split_once("ts_reader.run(") else {
                    continue;
                };
                let Ok(data) = serde_json::Deserializer::from_str(v.trim_start())
                    .into_iter::<Value>()
                    .next()
                    .ok_or_else(invalid)?
                else {
                    continue;
                };
                let Ok(sources) = rows(&data["sources"]) else {
                    continue;
                };
                for source in sources {
                    let Some(imgs) = source["images"].as_array().filter(|a| !a.is_empty()) else {
                        continue;
                    };
                    if imgs.len() > 2000 {
                        continue;
                    }
                    if let Ok(resolved) = imgs
                        .iter()
                        .map(|v| image(v.as_str().ok_or_else(invalid)?))
                        .collect::<Result<Vec<_>>>()
                    {
                        if !resolved.is_empty() {
                            return Ok(resolved);
                        }
                    }
                }
            }
            return Err(invalid());
        }
        if site == Manga::Leer {
            let (payload, order_key, scripts) = {
                let doc = Document::from(html.as_str());
                let payload = doc
                    .select("p#array_data")
                    .first()
                    .text()
                    .split_whitespace()
                    .collect::<String>();
                let order_key = doc
                    .select("meta[property='ad:check']")
                    .attr("content")
                    .map(|s| s.to_string())
                    .unwrap_or_default();
                let scripts = find_leer_reader_scripts(&self.base, &doc);
                (payload, order_key, scripts)
            };
            if !payload.is_empty() && !order_key.is_empty() {
                for script_url in scripts {
                    let cached = self
                        .leer_decoder
                        .as_ref()
                        .filter(|(url, at, _)| {
                            *url == script_url && at.elapsed() < Duration::from_secs(300)
                        })
                        .map(|(_, _, alphabets)| alphabets.clone());
                    if let Some((decoded, encoded)) = cached {
                        if let Ok(urls) =
                            decode_leer_images(&payload, &order_key, &decoded, &encoded, &final_url)
                        {
                            return Ok(urls);
                        }
                    }
                    let Ok(bytes) = self
                        .bytes(script_url.as_str(), "application/javascript", 4 << 20)
                        .await
                    else {
                        continue;
                    };
                    let alphabets = tokio::task::spawn_blocking(move || {
                        String::from_utf8(bytes)
                            .ok()
                            .and_then(|script| parse_leer_decoder(&script))
                    })
                    .await
                    .ok()
                    .flatten();
                    if let Some((decoded, encoded)) = alphabets {
                        if let Ok(urls) =
                            decode_leer_images(&payload, &order_key, &decoded, &encoded, &final_url)
                        {
                            self.leer_decoder =
                                Some((script_url, Instant::now(), (decoded, encoded)));
                            return Ok(urls);
                        }
                    }
                }
            }
            // Fallback to main#lcPages img
            let doc = Document::from(html.as_str());
            let mut pages = BTreeMap::new();
            for el in doc.select("main#lcPages img").iter() {
                let n = el
                    .attr("data-index")
                    .and_then(|s| s.parse::<usize>().ok())
                    .ok_or_else(invalid)?;
                let addr = el
                    .attr("data-src")
                    .or_else(|| el.attr("src"))
                    .ok_or_else(invalid)?;
                if n > 2000 || pages.insert(n, image(&addr)?).is_some() {
                    return Err(invalid());
                }
            }
            if pages.is_empty() || pages.keys().copied().ne(0..pages.len()) {
                return Err(invalid());
            }
            return Ok(pages.into_values().collect());
        }
        // ZonaTMO
        let doc = Document::from(html.as_str());
        let mut pages = BTreeMap::new();
        let mut seen_urls = HashSet::new();
        for el in doc.select("div.reader-img-wrap[data-page]").iter() {
            let Some(n) = el
                .attr("data-page")
                .and_then(|s| s.parse::<usize>().ok())
                .filter(|n| (1..=2000).contains(n))
            else {
                continue;
            };
            let img = el.select("img.reader-image");
            let img = if img.is_empty() {
                el.select("img")
            } else {
                img
            };
            let Some(addr) = img.attr("data-src").or_else(|| img.attr("src")) else {
                continue;
            };
            let Ok(url) = image(&addr) else {
                continue;
            };
            if seen_urls.insert(url.clone()) {
                pages.entry(n).or_insert(url);
            }
            if pages.len() >= 2000 {
                break;
            }
        }
        if pages.is_empty() {
            return Err(invalid());
        }
        Ok(pages.into_values().collect())
    }
}
