use crate::{capibara::Capibara, work, CommandResult};
use futures_util::{stream, StreamExt};
use serde_json::Value;
use std::{collections::HashSet, time::Duration};
use tauri::State;
use umd_core::{
    capibara::{chapter_number, Edition},
    CatalogItem, CatalogStore,
};
use umd_providers::{Entry, Provider, Site};
use uuid::Uuid;

const SITES: [&str; 6] = [
    "capibaratraductor",
    "olympusxyz",
    "manhwaweb",
    "leercapitulo",
    "zonatmo",
    "uchuujinmangas",
];
enum EditionRef {
    Capibara(Edition),
    Native(Entry),
}
struct Candidate {
    score: u32,
    site: &'static str,
    edition: EditionRef,
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
    let mut src = if let Some(s) = Site::parse(site) {
        Provider::open(s).await.ok()
    } else {
        None
    };
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
async fn load(
    c: Candidate,
    cap: &Capibara,
    number: &str,
) -> Option<(&'static str, String, CatalogItem, Vec<String>)> {
    let (id, item, pages) = match c.edition {
        EditionRef::Capibara(ref edition) => {
            let item = cap.edition_detail(edition).await.ok()?;
            if !has_chapter(&item, number) {
                return None;
            }
            let id = item.id().ok()?;
            let pages = cap.edition_pages(id, number.into()).await.ok()?;
            (id.to_string(), item, pages)
        }
        EditionRef::Native(e) => {
            let site = Site::parse(c.site)?;
            let mut p = Provider::open(site).await.ok()?;
            let item = p.detail(&e).await.ok()?;
            if !has_chapter(&item, number) {
                return None;
            }
            let pages = match site {
                Site::Olympus => p.reader_pages(&e, number).await,
                Site::Manhwa => p.manhwa_images(&e.external_id, number).await,
                Site::Manga(s) => p.manga_images(s, &e.external_id, number).await,
                _ => return None,
            }
            .ok()?;
            (e.external_id, item, pages)
        }
    };
    (!pages.is_empty()).then_some((c.site, id, item, pages))
}
#[tauri::command]
pub async fn find_chapter(
    id: Uuid,
    number: String,
    store: State<'_, CatalogStore>,
    cap: State<'_, Capibara>,
) -> CommandResult<Vec<String>> {
    let number = chapter_number(&Value::String(number)).ok_or("Número de capítulo inválido.")?;
    let store = store.inner().clone();
    let read = store.clone();
    let local = work(move || {
        read.item(id)
            .map_err(|e| e.to_string())?
            .and_then(|d| d.items.into_iter().next())
            .ok_or("El título ya no existe.".into())
    })
    .await?;
    if !local.media_type.eq_ignore_ascii_case("book") || !has_chapter(&local, &number) {
        return Err("El capítulo no está en esta edición.".into());
    }
    let key = local.key();
    let mut names = vec![local.name.clone()];
    for s in local
        .metadata
        .get("originalName")
        .and_then(Value::as_str)
        .into_iter()
        .chain(
            local
                .metadata
                .get("alternativeNames")
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
                .filter_map(|v| v["name"].as_str()),
        )
    {
        if !s.is_empty() && !names.iter().any(|v| v == s) && names.len() < 4 {
            names.push(s.into())
        }
    }
    let jobs =
        stream::iter(SITES.map(|site| {
            tokio::time::timeout(Duration::from_secs(30), candidates(site, &names, &cap))
        }))
        .buffer_unordered(3);
    let mut hits: Vec<_> = jobs
        .collect::<Vec<_>>()
        .await
        .into_iter()
        .filter_map(Result::ok)
        .flatten()
        .collect();
    hits.sort_by_key(|h| {
        (
            std::cmp::Reverse(h.score),
            SITES.iter().position(|s| *s == h.site),
        )
    });
    for c in hits.into_iter().take(10) {
        if let Ok(Some((site, external, item, pages))) =
            tokio::time::timeout(Duration::from_secs(25), load(c, &cap, &number)).await
        {
            let store = store.clone();
            let expected = key.clone();
            work(move || {
                store
                    .link_provider_edition(id, &expected, site, &external, item)
                    .map_err(|e| e.to_string())
            })
            .await?;
            return Ok(pages);
        }
    }
    Err("Los proveedores consultados no ofrecieron páginas para este capítulo.".into())
}
