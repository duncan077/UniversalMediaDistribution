use crate::{capibara::Capibara, work, CommandResult};
use futures_util::{stream, StreamExt};
use serde_json::Value;
use std::{collections::HashSet, time::Duration};
use tauri::{ipc::Channel, State};
use umd_core::{
    capibara::{chapter_number, Edition},
    CatalogItem, CatalogStore,
};
use umd_providers::Entry;
use uuid::Uuid;

const SITES: [&str; 6] = [
    "capibaratraductor",
    "olympusxyz",
    "manhwaweb",
    "leercapitulo",
    "zonatmo",
    "uchuujinmangas",
];
#[derive(Clone, serde::Deserialize, serde::Serialize)]
enum EditionRef {
    Capibara(Edition),
    Native(Entry),
}
struct Candidate {
    score: u32,
    site: &'static str,
    edition: EditionRef,
}

impl Candidate {
    fn matches_book_format(&self, local: &CatalogItem) -> bool {
        if !local.media_type.eq_ignore_ascii_case("book") {
            return true;
        }
        let EditionRef::Native(entry) = &self.edition else {
            return true;
        };
        entry.preview["subtype"]
            .as_str()
            .and_then(umd_core::provider_book_subtype)
            .is_none_or(|found| umd_core::provider_book_subtype(&local.subtype) == Some(found))
    }
}
const ANIME_SITES: &[&str] = &["animeav1", "jkanime", "animetmo", "framextv"];
const PROSE_SITES: &[&str] = &["manhwaweb", "zonatmo", "uchuujinmangas"];
const VIDEO_SITES: &[&str] = &[
    "framextv",
    "pelisplus",
    "pelisplus-to",
    "pelisflix-hd",
    "poseidon-hd2",
    "seriesflix",
    "doramasflix",
];

fn lookup_sites(item: &CatalogItem) -> &'static [&'static str] {
    if item.matches_subtype_group("manga") || item.matches_subtype_group("comic") {
        &SITES
    } else if item.matches_subtype_group("prose") {
        PROSE_SITES
    } else if item.matches_subtype_group("anime") {
        ANIME_SITES
    } else if item.matches_subtype_group("liveAction") {
        VIDEO_SITES
    } else {
        &[]
    }
}

fn names(item: &CatalogItem) -> Vec<String> {
    let mut names = vec![item.name.clone()];
    for name in item
        .metadata
        .get("originalName")
        .and_then(Value::as_str)
        .into_iter()
        .chain(
            item.metadata
                .get("alternativeNames")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
                .filter_map(|v| v["name"].as_str()),
        )
    {
        if !name.is_empty() && !names.iter().any(|v| v == name) && names.len() < 4 {
            names.push(name.into());
        }
    }
    names
}

pub(crate) fn same_edition(local: &CatalogItem, candidate: &CatalogItem) -> bool {
    let subtype = |item: &CatalogItem| {
        item.subtype
            .rsplit('.')
            .next()
            .unwrap_or("")
            .replace(['-', ' '], "_")
            .to_ascii_lowercase()
    };
    let a = local.metadata.get("releaseYear").and_then(Value::as_u64);
    let b = candidate
        .metadata
        .get("releaseYear")
        .and_then(Value::as_u64);
    local.media_type.eq_ignore_ascii_case(&candidate.media_type)
        && subtype(local) == subtype(candidate)
        && (a.is_none() || b.is_none() || a == b)
}

fn video_unit(
    item: &CatalogItem,
    number: Option<&str>,
    season: Option<u32>,
) -> Option<(String, Option<u32>)> {
    let standalone = matches!(
        item.subtype
            .rsplit('.')
            .next()?
            .to_ascii_lowercase()
            .as_str(),
        "movie" | "anime_movie" | "documentary" | "short"
    );
    let seasons = item
        .metadata
        .get("seasons")
        .and_then(Value::as_array)
        .into_iter()
        .flatten();
    let units = item
        .metadata
        .get("units")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .map(|unit| (unit, None))
        .chain(seasons.flat_map(|s| {
            let season = s["number"].as_u64().and_then(|n| u32::try_from(n).ok());
            s["units"]
                .as_array()
                .into_iter()
                .flatten()
                .map(move |unit| (unit, season))
        }));
    let wanted = number.and_then(|n| chapter_number(&Value::String(n.to_owned())));
    if number.is_none() && !standalone || number.is_some() && wanted.is_none() {
        return None;
    }
    units
        .filter(|(u, s)| {
            u["kind"] == "episode"
                && (s == &season
                    || season.is_none() && *s == Some(1)
                    || season == Some(1) && s.is_none())
                && wanted.as_ref().is_none_or(|wanted| {
                    chapter_number(&u["displayNumber"]).as_ref() == Some(wanted)
                })
        })
        .find_map(|(u, season)| Some((chapter_number(&u["displayNumber"])?, season)))
        .or_else(|| {
            (standalone && number.is_none() && season.is_none()).then(|| ("1".into(), None))
        })
}

async fn local_title(store: CatalogStore, id: Uuid) -> CommandResult<CatalogItem> {
    work(move || {
        store
            .item(id)
            .map_err(|e| e.to_string())?
            .and_then(|doc| doc.items.into_iter().next())
            .ok_or("El título ya no existe.".into())
    })
    .await
}

async fn detail(
    site: &str,
    edition: &EditionRef,
    cap: &Capibara,
) -> CommandResult<(String, CatalogItem)> {
    match edition {
        EditionRef::Capibara(edition) if site == "capibaratraductor" => {
            let mut item = cap.edition_detail(edition).await?;
            let external = item.id().map_err(|e| e.to_string())?.to_string();
            item.metadata.insert(
                "capibaraEdition".into(),
                serde_json::to_value(edition).map_err(|e| e.to_string())?,
            );
            Ok((external, item))
        }
        EditionRef::Native(entry) => {
            let item = crate::providers::open(site)
                .await?
                .detail(entry)
                .await
                .map_err(|_| "No se pudo consultar la edición del proveedor.")?;
            Ok((entry.external_id.clone(), item))
        }
        _ => Err("La edición seleccionada es inválida.".into()),
    }
}

#[derive(serde::Deserialize, serde::Serialize)]
pub struct UnitSource {
    site: String,
    title: String,
    edition: EditionRef,
}

async fn find_site_sources(
    site: &'static str,
    names: &[String],
    local: &CatalogItem,
    cap: &Capibara,
    number: Option<&str>,
    season: Option<u32>,
    hide_adult: bool,
) -> Vec<UnitSource> {
    tokio::time::timeout(Duration::from_secs(40), async {
        let mut candidates = candidates(site, names, cap).await;
        candidates.sort_by_key(|c| std::cmp::Reverse(c.score));
        let mut found = Vec::new();
        for candidate in candidates
            .into_iter()
            .filter(|candidate| candidate.matches_book_format(local))
            .take(3)
        {
            let Ok((_, item)) = detail(site, &candidate.edition, cap).await else {
                continue;
            };
            if !same_edition(local, &item)
                || (hide_adult && item.adult())
                || available_unit(&item, number, season).is_none()
            {
                continue;
            }
            found.push(UnitSource {
                site: site.into(),
                title: item.name,
                edition: candidate.edition,
            });
        }
        found
    })
    .await
    .unwrap_or_default()
}

fn available_unit(
    item: &CatalogItem,
    number: Option<&str>,
    season: Option<u32>,
) -> Option<(String, Option<u32>)> {
    if item.matches_subtype_group("manga") || item.matches_subtype_group("comic") {
        let number = chapter_number(&Value::String(number?.to_owned()))?;
        (season.is_none() && has_chapter(item, &number)).then_some((number, None))
    } else {
        video_unit(item, number, season)
    }
}

#[tauri::command]
pub async fn find_unit_sources(
    id: Uuid,
    number: Option<String>,
    season: Option<u32>,
    disabled_sites: Option<Vec<String>>,
    hide_adult: bool,
    matches: Channel<Vec<UnitSource>>,
    store: State<'_, CatalogStore>,
    cap: State<'_, Capibara>,
) -> CommandResult<()> {
    let local = local_title(store.inner().clone(), id).await?;
    if available_unit(&local, number.as_deref(), season).is_none() {
        return Err("La unidad no está en esta edición.".into());
    }
    let names = names(&local);
    let disabled = disabled_sites.unwrap_or_default();
    let jobs: Vec<&'static str> = lookup_sites(&local)
        .iter()
        .copied()
        .filter(|site| !disabled.iter().any(|id| id == site))
        .collect();
    let mut jobs = jobs.into_iter();
    let cap = cap.inner();
    let number = number.as_deref();
    let mut results = futures_util::stream::FuturesUnordered::new();
    for site in jobs.by_ref().take(3) {
        results.push(find_site_sources(
            site, &names, &local, cap, number, season, hide_adult,
        ));
    }
    while let Some(found) = results.next().await {
        if !found.is_empty() && matches.send(found).is_err() {
            break;
        }
        if let Some(site) = jobs.next() {
            results.push(find_site_sources(
                site, &names, &local, cap, number, season, hide_adult,
            ));
        }
    }
    Ok(())
}

#[tauri::command]
pub async fn select_unit_source(
    id: Uuid,
    number: Option<String>,
    season: Option<u32>,
    choice: UnitSource,
    hide_adult: bool,
    store: State<'_, CatalogStore>,
    cap: State<'_, Capibara>,
) -> CommandResult<Value> {
    let store = store.inner().clone();
    let local = local_title(store.clone(), id).await?;
    if !lookup_sites(&local).contains(&choice.site.as_str())
        || available_unit(&local, number.as_deref(), season).is_none()
    {
        return Err("La unidad no está en esta edición.".into());
    }
    let (external, fresh) = detail(&choice.site, &choice.edition, &cap).await?;
    if !same_edition(&local, &fresh) || hide_adult && fresh.adult() {
        return Err("La edición seleccionada es inválida.".into());
    }
    let (number, season) = available_unit(&fresh, number.as_deref(), season)
        .ok_or("La unidad no está en esta edición.")?;
    let content = if matches!(&choice.edition, EditionRef::Capibara(_)) {
        serde_json::to_value(
            cap.edition_pages(fresh.id().map_err(|e| e.to_string())?, number)
                .await?,
        )
        .map_err(|e| e.to_string())?
    } else {
        crate::providers::provider_content(&choice.site, &external, &fresh.name, &number, season)
            .await?
    };
    if content.as_array().is_none_or(Vec::is_empty) {
        return Err("La fuente no ofreció contenido para esta unidad.".into());
    }
    work(move || {
        store
            .link_provider_edition(id, &local.key(), &choice.site, &external, fresh)
            .map(|_| ())
            .map_err(|e| e.to_string())
    })
    .await?;
    Ok(content)
}
fn normalized(s: &str) -> Vec<char> {
    umd_core::normalize(s)
        .chars()
        .filter(|c| c.is_alphanumeric())
        .take(200)
        .collect()
}
fn similarity(a: &[char], b: &[char]) -> u32 {
    if a.is_empty() || b.is_empty() {
        return 0;
    }
    let mut row: Vec<usize> = (0..=b.len()).collect();
    for (i, c) in a.iter().enumerate() {
        let mut prev = row[0];
        row[0] = i + 1;
        for (j, d) in b.iter().enumerate() {
            let old = row[j + 1];
            row[j + 1] = (prev + usize::from(c != d)).min(row[j] + 1).min(old + 1);
            prev = old;
        }
    }
    let size = a.len().max(b.len());
    ((size - row[b.len()]) * 1000 / size) as u32
}
async fn candidates(site: &'static str, names: &[String], cap: &Capibara) -> Vec<Candidate> {
    let mut src = crate::providers::open(site).await.ok();
    let terms: Vec<_> = names.iter().map(|s| normalized(s)).collect();
    let mut choices = vec![];
    let mut seen = HashSet::new();
    for term in names {
        let rows: Vec<_> = if site == "capibaratraductor" {
            let Ok(page) = cap.search(term, 1).await else {
                break;
            };
            page.items
                .into_iter()
                .map(|h| {
                    (
                        format!("{}:{}", h.edition.joint, h.edition.id),
                        h.title,
                        EditionRef::Capibara(h.edition),
                    )
                })
                .collect()
        } else if let Some(source) = src.as_mut() {
            let Ok(page) = source.search(term, 1).await else {
                continue;
            };
            page.items
                .into_iter()
                .filter_map(|e| {
                    Some((
                        e.external_id.clone(),
                        e.preview["name"].as_str()?.to_owned(),
                        EditionRef::Native(e),
                    ))
                })
                .collect()
        } else {
            break;
        };
        for (key, name, edition) in rows {
            if !seen.insert(key) {
                continue;
            }
            let name = normalized(&name);
            let score = terms
                .iter()
                .map(|s| similarity(s, &name))
                .max()
                .unwrap_or(0);
            if score >= 720 {
                choices.push(Candidate {
                    score,
                    site,
                    edition,
                })
            }
        }
        if choices.iter().any(|c| c.score == 1000) {
            break;
        }
    }
    choices
}
fn has_chapter(item: &CatalogItem, n: &str) -> bool {
    item.metadata
        .get("units")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .any(|u| {
            u["kind"] == "chapter" && chapter_number(&u["displayNumber"]).as_deref() == Some(n)
        })
}
pub(crate) async fn discover_best_match(
    local: &CatalogItem,
    cap: &Capibara,
    disabled_sites: &[String],
) -> Option<(&'static str, String, CatalogItem)> {
    let sites = lookup_sites(local);
    let names = names(local);
    let mut hits = Vec::new();
    let futures: Vec<_> = sites
        .iter()
        .copied()
        .filter(|site| !disabled_sites.iter().any(|disabled| disabled == site))
        .map(|site| tokio::time::timeout(Duration::from_secs(15), candidates(site, &names, cap)))
        .collect();
    let mut jobs = stream::iter(futures).buffer_unordered(3);
    while let Some(batch) = jobs.next().await {
        if let Ok(batch) = batch {
            hits.extend(
                batch
                    .into_iter()
                    .filter(|c| c.score >= 900 && c.matches_book_format(local)),
            );
        }
    }
    hits.sort_by_key(|h| {
        (
            std::cmp::Reverse(h.score),
            sites.iter().position(|s| *s == h.site),
        )
    });
    for c in hits.into_iter().take(4) {
        let fetched =
            tokio::time::timeout(Duration::from_secs(15), detail(c.site, &c.edition, cap)).await;
        if let Ok(Ok((external, item))) = fetched {
            if same_edition(local, &item) {
                return Some((c.site, external, item));
            }
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn discovery_matches_the_exact_adaptation_and_episode() {
        let item = |value| serde_json::from_value::<CatalogItem>(value).unwrap();
        let manga = item(
            json!({"name":"Obra","mediaType":"book","subtype":"book.manga",
            "units":[{"kind":"chapter","displayNumber":"1.5"}]}),
        );
        let novel = item(
            json!({"name":"Obra","mediaType":"book","subtype":"light_novel",
            "units":[{"kind":"chapter","displayNumber":"1.5"}]}),
        );
        assert!(!lookup_sites(&novel).contains(&"capibaratraductor"));
        assert!(available_unit(&novel, Some("1.5"), None).is_none());
        assert!(!same_edition(&manga, &novel));
        let candidate = Candidate {
            score: 1000,
            site: "zonatmo",
            edition: EditionRef::Native(Entry {
                external_id: "novel/191/obra".into(),
                preview: json!({"name":"Obra","subtype":"book.light_novel"}),
            }),
        };
        assert!(!candidate.matches_book_format(&manga));
        assert!(candidate.matches_book_format(&novel));
        assert!(available_unit(&manga, Some("01.50"), None).is_some());
        let series = item(json!({"name":"Obra","mediaType":"movie","subtype":"series",
            "seasons":[{"number":1,"units":[{"kind":"episode","displayNumber":"2"}]},
                {"number":2,"units":[{"kind":"episode","displayNumber":"3"}]}]}));
        assert_eq!(
            available_unit(&series, Some("02"), None),
            Some(("2".into(), Some(1)))
        );
        assert!(available_unit(&series, Some("2"), Some(2)).is_none());
        assert!(available_unit(&series, Some("3"), Some(2)).is_some());
    }
}
