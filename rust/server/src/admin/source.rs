use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{collections::BTreeMap, time::Duration};
use umd_core::{capibara, catalog_metadata_only, CatalogDocument, CatalogItem, MAX_DOCUMENT_BYTES};
use umd_providers::{Provider, Site};
use url::Url;

pub(super) const PROVIDERS: &[(&str, &str)] = &[
    ("animetmo", "AnimeTMO"),
    ("olympusxyz", "OlympusXYZ"),
    ("animeav1", "AnimeAV1"),
    ("framextv", "FramexTV"),
    ("manhwaweb", "ManhwaWeb"),
    ("capibaratraductor", "CapibaraTraductor"),
    ("doramasflix", "DoramasFlix"),
    ("leercapitulo", "LeerCapitulo"),
    ("zonatmo", "ZonaTMO"),
    ("uchuujinmangas", "UchuujinMangas"),
    ("jkanime", "JKAnime"),
    ("pelisplus", "PelisPlus"),
    ("pelisplus-to", "PelisPlus.to"),
    ("pelisflix-hd", "Pelisflix HD"),
    ("poseidon-hd2", "Poseidon HD2"),
    ("seriesflix", "Seriesflix"),
    ("umd-json-api", "Catálogo UMD externo"),
];
#[derive(Clone)]
pub(super) struct Config {
    #[cfg(test)]
    pub fixture_address: Option<std::net::SocketAddr>,
    pub disabled: Vec<String>,
    pub(super) bases: BTreeMap<String, Url>,
    pub pages: u32,
    pub budget: Duration,
    pub operation: Duration,
    pub detail: Duration,
}
impl Config {
    pub fn read() -> Result<Self, String> {
        let var = |name: &str| std::env::var(name).ok().filter(|v| !v.trim().is_empty());
        let number = |name: &str, default: u64, lo: u64, hi: u64| match var(name) {
            None => Ok(default),
            Some(s) => s
                .trim()
                .parse::<u64>()
                .ok()
                .filter(|n| (lo..=hi).contains(n))
                .ok_or_else(|| format!("{name} must be between {lo} and {hi}")),
        };
        let disabled: Vec<_> = var("UMD_CRAWLER_DISABLED_PROVIDERS")
            .unwrap_or_default()
            .split(',')
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .map(str::to_owned)
            .collect();
        if disabled
            .iter()
            .any(|s| !PROVIDERS.iter().any(|(id, _)| id == s))
        {
            return Err("Unknown disabled provider".into());
        }
        let mut bases = BTreeMap::new();
        for &(id, _) in PROVIDERS {
            if let Some(raw) = var(&format!(
                "UMD_CRAWLER_BASE_{}",
                id.replace('-', "_").to_uppercase()
            ))
            .or_else(|| {
                Site::parse(id)
                    .map(|site| site.base().to_owned())
                    .or_else(|| {
                        (id == "capibaratraductor").then(|| "https://capibaratraductor.com/".into())
                    })
            }) {
                let url = Url::parse(&format!("{}/", raw.trim_end_matches('/')))
                    .map_err(|_| format!("Invalid URL for {id}"))?;
                if !matches!(url.scheme(), "http" | "https")
                    || url.host_str().is_none()
                    || !url.username().is_empty()
                    || url.password().is_some()
                    || url.query().is_some()
                    || url.fragment().is_some()
                {
                    return Err(format!("Invalid URL for {id}"));
                }
                bases.insert(id.into(), url);
            }
        }
        let operation = number("UMD_CRAWLER_OPERATION_SECONDS", 180, 10, 1200)?;
        Ok(Self {
            #[cfg(test)]
            fixture_address: None,
            disabled,
            bases,
            pages: number("UMD_CRAWLER_PAGES_PER_PROVIDER", 2, 1, 100)? as u32,
            budget: Duration::from_secs(number("UMD_CRAWLER_MAX_MINUTES", 45, 1, 50)? * 60),
            operation: Duration::from_secs(operation),
            detail: Duration::from_secs(number(
                "UMD_CRAWLER_DETAIL_SECONDS",
                if var("UMD_CRAWLER_OPERATION_SECONDS").is_some() {
                    operation
                } else {
                    600
                },
                10,
                1200,
            )?),
        })
    }
    pub fn availability(&self, id: &str) -> Option<&'static str> {
        if self.disabled.iter().any(|v| v == id) {
            Some("disabled")
        } else if !self.bases.contains_key(id) {
            Some("configuration_required")
        } else {
            None
        }
    }
}
#[derive(Debug)]
pub(super) struct Failure {
    pub kind: &'static str,
    pub retry: i64,
    pub status: Option<u16>,
}
impl Failure {
    pub fn new(kind: &'static str) -> Self {
        Self {
            kind,
            retry: 0,
            status: None,
        }
    }
}
type ResultOf<T> = Result<T, Failure>;
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Preview {
    pub external_id: String,
    pub preview: Value,
}
#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Reply {
    pub items: Option<Vec<Preview>>,
    pub next: Option<String>,
    pub item: Option<CatalogItem>,
}
pub(super) enum Source {
    Native(Box<Provider>),
    Snapshot {
        url: Url,
        client: reqwest::Client,
        items: Option<BTreeMap<String, Value>>,
    },
    Capibara {
        base: Url,
        client: reqwest::Client,
    },
}
async fn fetch(request: reqwest::RequestBuilder) -> ResultOf<Vec<u8>> {
    let mut res = request.send().await.map_err(|_| Failure::new("network"))?;
    if !res.status().is_success() {
        let status = res.status().as_u16();
        if status == 429 || status == 503 && res.headers().contains_key("retry-after") {
            let delay = res
                .headers()
                .get("retry-after")
                .and_then(|h| h.to_str().ok())
                .and_then(|s| {
                    s.parse::<i64>().ok().or_else(|| {
                        chrono::DateTime::parse_from_rfc2822(s)
                            .ok()
                            .map(|d| d.timestamp() - chrono::Utc::now().timestamp())
                    })
                })
                .unwrap_or(3600);
            return Err(Failure {
                kind: "throttled",
                status: Some(status),
                retry: delay.clamp(1, 86400),
            });
        }
        return Err(Failure {
            status: Some(status),
            retry: 0,
            kind: match status {
                404 | 410 => "not_found",
                401 | 403 => "challenge_or_access_denied",
                _ => "network",
            },
        });
    }
    let mut bytes = Vec::new();
    while let Some(chunk) = res.chunk().await.map_err(|_| Failure::new("network"))? {
        if chunk.len() > MAX_DOCUMENT_BYTES - bytes.len() {
            return Err(Failure::new("parse_or_storage_error"));
        }
        bytes.extend_from_slice(&chunk);
    }
    Ok(bytes)
}
impl Source {
    pub async fn open(id: &str, config: &Config, interval: u32) -> ResultOf<Self> {
        if matches!(id, "umd-json-api" | "capibaratraductor") || Site::parse(id).is_some() {
            let base = config
                .bases
                .get(id)
                .ok_or(Failure::new("configuration_required"))?
                .clone();
            if let Some(site) = Site::parse(id).filter(|s| {
                base.as_str() == s.base()
                    || (*s == Site::Framex && base.as_str() == "https://framextv.xyz/")
            }) {
                let p = Provider::open_with_interval(site, Duration::from_secs(interval.into()))
                    .await
                    .map_err(|e| Failure {
                        kind: e.kind,
                        retry: e.retry,
                        status: e.status,
                    })?;
                return Ok(Self::Native(Box::new(p)));
            }
            if !umd_core::public_url::web_url(&base) {
                return Err(Failure::new("configuration_required"));
            }
            let host = base.host_str().unwrap().trim_matches(['[', ']']);
            let port = base.port_or_known_default().unwrap();
            let resolve = || async {
                let addresses: Vec<_> = tokio::net::lookup_host((host, port))
                    .await
                    .map_err(|_| Failure::new("network"))?
                    .collect();
                if addresses.is_empty()
                    || addresses
                        .iter()
                        .any(|a| !umd_core::public_url::public_ip(a.ip()))
                {
                    return Err(Failure::new("configuration_required"));
                }
                Ok(addresses)
            };
            #[cfg(test)]
            let addresses = if let Some(address) = config.fixture_address {
                vec![address]
            } else {
                resolve().await?
            };
            #[cfg(not(test))]
            let addresses = resolve().await?;
            let client = reqwest::Client::builder()
                .user_agent(concat!(
                    "UniversalMediaDistribution/",
                    env!("CARGO_PKG_VERSION")
                ))
                .cookie_store(true)
                .no_proxy()
                .redirect(reqwest::redirect::Policy::none())
                .timeout(Duration::from_secs(90))
                .resolve_to_addrs(host, &addresses)
                .build()
                .map_err(|_| Failure::new("configuration_required"))?;
            if let Some(site) = Site::parse(id) {
                return Ok(Self::Native(Box::new(Provider::new(
                    site,
                    base,
                    client,
                    Duration::from_secs(interval.into()),
                ))));
            }
            return Ok(if id == "umd-json-api" {
                Self::Snapshot {
                    url: base
                        .join("current")
                        .map_err(|_| Failure::new("configuration_required"))?,
                    client,
                    items: None,
                }
            } else {
                Self::Capibara { base, client }
            });
        }
        Err(Failure::new("configuration_required"))
    }
    pub async fn command(
        &mut self,
        op: &str,
        cursor: Option<&str>,
        entry: Option<&Preview>,
    ) -> ResultOf<Reply> {
        let invalid = || Failure::new("parse_or_storage_error");
        match self {
            Self::Native(provider) => {
                let failure = |err: umd_providers::Failure| Failure {
                    kind: err.kind,
                    retry: err.retry,
                    status: err.status,
                };
                if let Some(e) = entry {
                    let item = provider
                        .detail(&umd_providers::Entry {
                            external_id: e.external_id.clone(),
                            preview: e.preview.clone(),
                        })
                        .await
                        .map_err(failure)?;
                    return Ok(Reply {
                        item: Some(item),
                        ..Default::default()
                    });
                }
                let (rows, next) = if op == "updates" {
                    (provider.updates().await.map_err(failure)?, None)
                } else {
                    let page = provider.page(cursor).await.map_err(failure)?;
                    (page.items, page.next)
                };
                Ok(Reply {
                    items: Some(
                        rows.into_iter()
                            .map(|e| Preview {
                                external_id: e.external_id,
                                preview: e.preview,
                            })
                            .collect(),
                    ),
                    next,
                    ..Default::default()
                })
            }
            Self::Snapshot { url, client, items } => {
                if let Some(e) = entry {
                    return Ok(Reply {
                        item: Some(
                            serde_json::from_value(e.preview.clone()).map_err(|_| invalid())?,
                        ),
                        ..Default::default()
                    });
                }
                if op == "updates" {
                    return Ok(Reply {
                        items: Some(vec![]),
                        ..Default::default()
                    });
                }
                if items.is_none() {
                    let bytes = fetch(client.get(url.clone())).await?;
                    let doc = CatalogDocument::parse(&bytes).map_err(|_| invalid())?;
                    let mut map = BTreeMap::new();
                    for mut item in doc.items.into_iter().filter(|i| !i.deleted) {
                        let id = item.id().map_err(|_| invalid())?;
                        item.sync_id = Some(id);
                        map.insert(id.to_string(), json!(catalog_metadata_only(item)));
                    }
                    *items = Some(map);
                }
                let mut iter = items.as_ref().unwrap().range((
                    cursor.map_or(std::ops::Bound::Unbounded, |c| {
                        std::ops::Bound::Excluded(c.to_owned())
                    }),
                    std::ops::Bound::Unbounded,
                ));
                let page: Vec<_> = iter
                    .by_ref()
                    .take(500)
                    .map(|(k, v)| Preview {
                        external_id: k.clone(),
                        preview: v.clone(),
                    })
                    .collect();
                let next = iter
                    .next()
                    .and_then(|_| page.last().map(|p| p.external_id.clone()));
                Ok(Reply {
                    items: Some(page),
                    next,
                    ..Default::default()
                })
            }
            Self::Capibara { base, client } => {
                let ed: Option<capibara::Edition> = entry
                    .map(|e| serde_json::from_value(e.preview.clone()))
                    .transpose()
                    .map_err(|_| invalid())?;
                let page = cursor
                    .unwrap_or("1")
                    .parse::<u32>()
                    .map_err(|_| invalid())?;
                let request = if let Some(e) = &ed {
                    if !e.valid() {
                        return Err(invalid());
                    }
                    let part = if e.joint { "joint" } else { "manga-custom" };
                    let req = client.get(
                        base.join(&format!("api/{part}/{}", e.slug))
                            .map_err(|_| invalid())?,
                    );
                    if e.joint {
                        req
                    } else {
                        req.header("x-organization", &e.scan)
                    }
                } else {
                    client
                        .get(base.join("api/manga-custom").map_err(|_| invalid())?)
                        .query(&[
                            ("page", page.to_string()),
                            ("limit", "20".into()),
                            ("order", "latest".into()),
                            ("nsfw", "false".into()),
                        ])
                };
                let envelope: Value =
                    serde_json::from_slice(&fetch(request).await?).map_err(|_| invalid())?;
                if envelope["status"] != true {
                    return Err(invalid());
                }
                if let Some(e) = ed {
                    return Ok(Reply {
                        item: Some(capibara::detail(&e, &envelope["data"]).map_err(|_| invalid())?),
                        ..Default::default()
                    });
                }
                let listing = capibara::listing(&envelope["data"], page).map_err(|_| invalid())?;
                Ok(Reply {
                    items: Some(
                        listing
                            .items
                            .into_iter()
                            .map(|h| Preview {
                                external_id: format!(
                                    "{}{}",
                                    if h.edition.joint { "joint-" } else { "" },
                                    h.edition.id
                                ),
                                preview: json!(h.edition),
                            })
                            .collect(),
                    ),
                    next: listing.next_page.map(|n| n.to_string()),
                    ..Default::default()
                })
            }
        }
    }
}
