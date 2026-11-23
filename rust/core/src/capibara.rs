use crate::CatalogItem;
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::collections::BTreeMap;
use url::Url;
use uuid::Uuid;
// A provider-specific UUIDv8 prefix leaves 32 bits for the edition ID. Catalog
// export and account backup already carry syncId, including across .NET clients.
pub const EDITION_PREFIX: u128 = 0x729ef15d_b486_84f4_983c_ef8900000000;
pub const JOINT_PREFIX: u128 = 0x729ef15d_b486_84f4_983c_ef8a00000000;

pub fn edition_id(id: Uuid) -> Option<u32> {
    let bits = id.as_u128();
    let edition = bits as u32;
    (matches!(bits & !u128::from(u32::MAX), EDITION_PREFIX | JOINT_PREFIX) && edition != 0)
        .then_some(edition)
}

#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
pub struct Edition {
    pub id: u32,
    #[serde(default)]
    pub joint: bool,
    pub scan: String,
    pub slug: String,
}

#[derive(Serialize)]
pub struct Hit {
    pub edition: Edition,
    pub title: String,
    pub group: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SearchPage {
    pub items: Vec<Hit>,
    pub next_page: Option<u32>,
}

pub fn valid_slug(s: &str) -> bool {
    (1..=200).contains(&s.len())
        && s.as_bytes()[0].is_ascii_alphanumeric()
        && s.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-')
}

impl Edition {
    pub fn read(value: &Value) -> Option<Self> {
        let joint_id = value["id"].as_str().and_then(|s| s.strip_prefix("joint-"));
        let (id, slug) = if let Some(n) = joint_id {
            (n.parse().ok()?, value["_jointSlug"].as_str()?)
        } else {
            (
                value["id"].as_u64()?.try_into().ok()?,
                value["manga"]["slug"].as_str()?,
            )
        };
        let edition = Self {
            id,
            joint: joint_id.is_some(),
            scan: value["organization"]["slug"].as_str()?.into(),
            slug: slug.into(),
        };
        edition.valid().then_some(edition)
    }

    pub fn valid(&self) -> bool {
        self.id > 0 && valid_slug(&self.scan) && valid_slug(&self.slug)
    }
}

pub fn available_manga(row: &Value) -> bool {
    !["isNSFW", "requireLogin", "loggedInOnly"]
        .iter()
        .any(|k| row[k].as_bool() == Some(true))
        && row["isPublic"].as_bool() != Some(false)
        && ![&row["workType"], &row["manga"]["bookType"]["code"]]
            .iter()
            .any(|v| {
                matches!(
                    v.as_str()
                        .unwrap_or_default()
                        .trim()
                        .to_ascii_lowercase()
                        .as_str(),
                    "novel" | "light-novel" | "book" | "short-story"
                )
            })
}

pub fn image_url(v: &Value) -> Option<String> {
    let url = Url::parse(v.as_str()?).ok()?;
    (url.scheme() == "https"
        && url.username().is_empty()
        && url.password().is_none()
        && url.domain().is_some_and(|host| {
            host == "capibaratraductor.com" || host.ends_with(".capibaratraductor.com")
        }))
    .then(|| url.to_string())
}

pub fn chapter_number(v: &Value) -> Option<String> {
    let raw = if let Some(s) = v.as_str() {
        s.to_owned()
    } else {
        v.as_number()?.to_string()
    };
    let (whole, part) = raw.split_once('.').unwrap_or((&raw, ""));
    if raw.len() > 24
        || whole.is_empty()
        || !whole
            .bytes()
            .chain(part.bytes())
            .all(|b| b.is_ascii_digit())
    {
        return None;
    }
    let whole = whole.parse::<u64>().ok()?;
    let part = part.trim_end_matches('0');
    Some(if part.is_empty() {
        whole.to_string()
    } else {
        format!("{whole}.{part}")
    })
}

pub fn listing(data: &Value, page: u32) -> Result<SearchPage, String> {
    let rows = data["items"]
        .as_array()
        .ok_or("La respuesta no incluye la lista de títulos.")?;
    let items = rows
        .iter()
        .filter(|r| available_manga(r))
        .filter_map(|row| {
            let edition = Edition::read(row)?;
            let title = row["title"].as_str()?.trim();
            if title.is_empty() {
                return None;
            }
            let members: Vec<_> = row["_jointMembers"]
                .as_array()
                .into_iter()
                .flatten()
                .filter_map(|m| m["name"].as_str())
                .collect();
            let group = if edition.joint && !members.is_empty() {
                members.join(" + ")
            } else {
                row["organization"]["name"]
                    .as_str()
                    .unwrap_or(&edition.scan)
                    .to_owned()
            };
            Some(Hit {
                edition,
                title: title.into(),
                group,
            })
        })
        .collect();
    let next_page = (data["maxPage"].as_u64().unwrap_or(1) > u64::from(page))
        .then(|| page.checked_add(1))
        .flatten();
    Ok(SearchPage { items, next_page })
}
pub fn detail(edition: &Edition, row: &Value) -> Result<CatalogItem, String> {
    let same = if edition.joint {
        row["id"] == edition.id
            && row["slug"] == edition.slug
            && (row["organization"]["slug"].is_null()
                || row["organization"]["slug"].as_str() == Some(edition.scan.as_str()))
    } else {
        Edition::read(row).as_ref() == Some(edition)
    };
    if !same || !available_manga(row) {
        return Err("La edición seleccionada ya no está disponible.".into());
    }
    let chapters = row["chapters"]
        .as_array()
        .ok_or("El proveedor no devolvió los capítulos.")?;
    if chapters.len() > 20_000 {
        return Err("El título supera el límite de capítulos.".into());
    }
    let mut numbered = BTreeMap::new();
    let now = Utc::now();
    for c in chapters {
        if c["isUnreleased"] == true
            || c["hasAccess"] == false
            || !c["hiddenAt"].is_null()
            || c["releasedAt"]
                .as_str()
                .and_then(|date| DateTime::parse_from_rfc3339(date).ok())
                .is_some_and(|date| date > now)
        {
            continue;
        }
        if let Some(number) = chapter_number(&c["number"]) {
            let (whole, fractional) = number.split_once('.').unwrap_or((&number, ""));
            let key = (whole.len(), whole.to_owned(), fractional.to_owned());
            numbered.entry(key).or_insert((number, c["title"].as_str()));
        }
    }
    let units: Vec<_> = numbered.into_values().enumerate().map(|(i,(number,title))|
            json!({"kind":"chapter","displayNumber":number,"name":title,"sortOrder":i})).collect();
    let genres: Vec<_> = row["genres"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|v| v["name"].as_str())
        .collect();
    let prefix = if edition.joint {
        JOINT_PREFIX
    } else {
        EDITION_PREFIX
    };
    let id = Uuid::from_u128(prefix | u128::from(edition.id));
    let item: CatalogItem = serde_json::from_value(json!({
            "syncId":id,"name":row["title"],"mediaType":"book","subtype":"manga","units":units,
            "description":row["description"].as_str().or_else(|| row["manga"]["description"].as_str()),
            "thumbnailRef":image_url(&row["imageUrl"]), "backdropRef":image_url(&row["bannerUrl"]), "genres":genres
        })).map_err(|e| e.to_string())?;
    Ok(item)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn joint_detail_verifies_organization_slug_when_present() {
        let edition = Edition {
            id: 42,
            joint: true,
            scan: "scan-a".into(),
            slug: "obra-x".into(),
        };
        let matching = json!({
            "id": 42,
            "slug": "obra-x",
            "title": "Obra X",
            "organization": { "slug": "scan-a" },
            "chapters": []
        });
        assert!(detail(&edition, &matching).is_ok());
        let without_org = json!({
            "id": 42,
            "slug": "obra-x",
            "title": "Obra X",
            "chapters": []
        });
        assert!(detail(&edition, &without_org).is_ok());
        let mismatched = json!({
            "id": 42,
            "slug": "obra-x",
            "title": "Obra X",
            "organization": { "slug": "scan-b" },
            "chapters": []
        });
        assert!(detail(&edition, &mismatched).is_err());
    }
}
