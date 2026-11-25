use super::{normalize_timestamp, Result};
use rusqlite::{params, Connection, OpenFlags};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{collections::BTreeMap, path::Path};
use url::Url;
use uuid::Uuid;

fn profile_settings(db: &Connection) -> Result<BTreeMap<Uuid, BTreeMap<String, String>>> {
    let mut settings = BTreeMap::<_, BTreeMap<_, _>>::new();
    if !db.query_row(
        "SELECT EXISTS(SELECT 1 FROM main.sqlite_master WHERE type='table' AND name='profile_settings')",
        [], |r| r.get::<_, bool>(0),
    )? {
        return Ok(settings);
    }
    let mut stmt = db.prepare("SELECT s.profile_id,s.key,s.value FROM main.profile_settings s JOIN main.profiles p ON p.id=s.profile_id
        WHERE s.key IN ('remote_server_url','remote_online_catalog_enabled','remote_session_service_url','remote_session_access_token',
            'remote_session_account_id','remote_session_username','remote_session_display_name','remote_session_expires_at')")?;
    let mut rows = stmt.query([])?;
    while let Some(row) = rows.next()? {
        if let Ok(id) = Uuid::parse_str(&row.get::<_, String>(0)?) {
            settings
                .entry(id)
                .or_default()
                .insert(row.get(1)?, row.get(2)?);
        }
    }
    Ok(settings)
}

fn setting(db: &Connection, key: &str) -> Result<Option<String>> {
    use rusqlite::OptionalExtension;
    Ok(db
        .query_row(
            "SELECT value FROM application_settings WHERE key=?1",
            [key],
            |r| r.get(0),
        )
        .optional()?)
}

fn property<'a>(object: &'a Value, spelling: &str) -> &'a Value {
    object
        .as_object()
        .and_then(|values| {
            values
                .iter()
                .find(|(key, _)| key.eq_ignore_ascii_case(spelling))
                .map(|(_, v)| v)
        })
        .unwrap_or(&Value::Null)
}

fn server_url(raw: &str, session: bool) -> Option<Url> {
    let mut url = Url::parse(raw.trim()).ok()?;
    let loopback = matches!(
        url.host_str(),
        Some("localhost" | "127.0.0.1" | "[::1]" | "::1")
    );
    if !(url.scheme() == "https" || url.scheme() == "http" && (!session || loopback))
        || url.host().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
    {
        return None;
    }
    let path = url.path().trim_end_matches('/');
    url.set_path(&format!(
        "{}/",
        path.strip_suffix("/api/v1").unwrap_or(path)
    ));
    Some(url)
}

pub(super) fn import(source: &Connection, target: &Connection, active: Uuid) -> Result<()> {
    let profiles = profile_settings(source)?;
    let mut prefs = BTreeMap::new();
    let mut global = BTreeMap::new();
    if let Some(mode) = setting(source, "content_filter_mode")? {
        let mode = mode.to_ascii_lowercase();
        if matches!(mode.as_str(), "off" | "basic" | "standard" | "complete") {
            global.insert("player_ad_filter", mode);
        }
    }
    if let Some(raw) = setting(source, "content_filter_hide_adult")? {
        if let Ok(hidden) = raw.to_ascii_lowercase().parse::<bool>() {
            prefs.insert("umd-hide-adult".to_owned(), hidden.to_string());
        }
    }
    let mut locations = vec![];
    if let Some(stored) = setting(source, "content_source_provider_settings")?
        .and_then(|s| serde_json::from_str::<Value>(&s).ok())
    {
        for item in property(&stored, "providers")
            .as_array()
            .into_iter()
            .flatten()
            .take(32)
        {
            let id = property(item, "id").as_str().unwrap_or_default();
            if id.is_empty()
                || id.len() > 96
                || !id.bytes().all(|c| {
                    c.is_ascii_lowercase() || c.is_ascii_digit() || [b'-', b'_'].contains(&c)
                })
            {
                continue;
            }
            let kind = property(item, "kind");
            if kind == 3 || kind.as_str().is_some_and(|s| s.eq_ignore_ascii_case("api")) {
                let custom;
                let id = if id == "umd-json-api" {
                    custom = format!(
                        "json-{}",
                        Uuid::new_v5(&Uuid::NAMESPACE_URL, b"umd-json-api")
                    );
                    custom.as_str()
                } else {
                    id
                };
                if let Some(enabled) = property(item, "isEnabled").as_bool() {
                    prefs.insert(format!("provider-enabled-{id}"), enabled.to_string());
                }
                let Some(name) = property(item, "displayName").as_str().filter(|s| {
                    !s.is_empty() && s.len() <= 128 && !s.chars().any(char::is_control)
                }) else {
                    continue;
                };
                let Some(base) = property(item, "baseUrl")
                    .as_str()
                    .and_then(|s| server_url(s, false))
                    .filter(|u| crate::public_url::web_url(u))
                else {
                    continue;
                };
                locations.push(json!({"id":id,"name":name,"base":base.as_str()}));
            }
            if kind == 4
                || kind
                    .as_str()
                    .is_some_and(|s| s.eq_ignore_ascii_case("liveTv"))
            {
                let id = match id {
                    "iptv-org-argentina" => "playlist",
                    "ip-tv-app-argentina" => "ip-tv-app",
                    "cable-vision-hd" | "cablevision-hd" => "cablevision",
                    "tv-por-internet-hd" => "tv-internet",
                    "tv-libre-futbol" => "futbol",
                    _ => continue,
                };
                if let Some(enabled) = property(item, "isEnabled").as_bool() {
                    prefs.insert(format!("tv-enabled-{id}"), enabled.to_string());
                }
            }
        }
    }
    if !locations.is_empty() {
        global.insert("provider-locations", serde_json::to_string(&locations)?);
    }
    if let Some(url) = profiles
        .get(&active)
        .and_then(|p| p.get("remote_server_url"))
        .and_then(|raw| server_url(raw, false))
    {
        global.insert("server_url", url.into());
    }
    for (key, value) in global {
        target.execute(
            "INSERT INTO rust_settings VALUES(?1,?2)",
            params![key, value],
        )?;
    }
    let mut stmt = target.prepare("SELECT id FROM rust_profiles")?;
    let mut rows = stmt.query([])?;
    let receipt = Uuid::new_v4();
    while let Some(row) = rows.next()? {
        let id: String = row.get(0)?;
        let mut values = prefs.clone();
        if let Some(enabled) = Uuid::parse_str(&id)
            .ok()
            .and_then(|id| profiles.get(&id))
            .and_then(|p| p.get("remote_online_catalog_enabled"))
            .and_then(|s| s.to_ascii_lowercase().parse::<bool>().ok())
        {
            values.insert(
                "online-catalog".into(),
                if enabled { "on" } else { "off" }.into(),
            );
        }
        if !values.is_empty() {
            target.execute(
                "INSERT INTO rust_settings VALUES(?1,?2)",
                params![
                    format!("legacy-prefs-{id}"),
                    json!({"receipt":receipt,"values":values}).to_string()
                ],
            )?;
        }
    }
    Ok(())
}

#[derive(Deserialize, Serialize)]
pub struct LegacyPrivateSettings {
    pub profile: Uuid,
    pub session: Option<Value>,
    pub metadata: Option<Value>,
}

/// Read secrets from a validated .NET backup for placement in the client's private profile files.
pub fn legacy_private_settings(
    source: &Path,
    profiles: &[Uuid],
) -> Result<Vec<LegacyPrivateSettings>> {
    let db = Connection::open_with_flags(source, OpenFlags::SQLITE_OPEN_READ_ONLY)?;
    db.execute_batch("PRAGMA trusted_schema=OFF")?;
    let values = profile_settings(&db)?;
    let metadata = if db.query_row("SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='application_settings')",
        [], |r| r.get::<_, bool>(0))? {
        setting(&db, "metadata_provider_settings")?.and_then(|s| serde_json::from_str::<Value>(&s).ok())
    } else {
        None
    };
    let metadata = metadata.map(|values| {
        let credential = |key| property(&values, key).as_str().filter(|s| s.len() <= 512 && !s.chars().any(char::is_whitespace)).unwrap_or_default();
        let language = property(&values, "preferredLanguage").as_str()
            .filter(|s| !s.is_empty() && s.len() <= 35 && s.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-'));
        json!({"tmdb":credential("tmdbApiKey"),"mal":credential("myAnimeListClientId"),"language":language})
    });
    Ok(profiles.iter().map(|profile| {
        let session = values.get(profile).and_then(|row| {
            let base = server_url(row.get("remote_session_service_url")?, true)?;
            let token = row.get("remote_session_access_token")?;
            if token.is_empty() || token.len() > 4096 || !token.bytes().all(|c| c.is_ascii_graphic()) {
                return None;
            }
            let account = Uuid::parse_str(row.get("remote_session_account_id")?).ok()?;
            let expires = row.get("remote_session_expires_at")?;
            if !chrono::DateTime::parse_from_rfc3339(expires).is_ok_and(|time| time > chrono::Utc::now()) {
                return None;
            }
            Some(json!({"base":base.as_str(),"account":account,"token":token,"expires":normalize_timestamp(expires),
               "user":{"username":row.get("remote_session_username"),"displayName":row.get("remote_session_display_name")},
               "incremental":false,"gzip":false,"automatic":false}))
        });
        LegacyPrivateSettings { profile: *profile, session, metadata: metadata.clone() }
    }).filter(|p| p.session.is_some() || p.metadata.is_some()).collect())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_only_requested_profiles_and_secure_session_origins() {
        let backup = tempfile::NamedTempFile::new().unwrap();
        let db = Connection::open(backup.path()).unwrap();
        db.execute_batch(
            "CREATE TABLE profiles(id TEXT PRIMARY KEY);
            CREATE TABLE profile_settings(profile_id TEXT,key TEXT,value TEXT);",
        )
        .unwrap();
        let profile = Uuid::from_u128(1);
        let unsafe_profile = Uuid::from_u128(2);
        for (id, server) in [
            (profile, "https://example.com/base/api/v1/"),
            (unsafe_profile, "http://example.com"),
        ] {
            db.execute("INSERT INTO profiles VALUES(?1)", [id.to_string()])
                .unwrap();
            for (key, value) in [
                (
                    "remote_session_account_id",
                    "00000000-0000-0000-0000-000000000003",
                ),
                ("remote_session_expires_at", "2099-01-01T00:00:00Z"),
                ("remote_session_service_url", server),
                ("remote_session_access_token", "fixture"),
            ] {
                db.execute(
                    "INSERT INTO profile_settings VALUES(?1,?2,?3)",
                    params![id.to_string(), key, value],
                )
                .unwrap();
            }
        }
        drop(db);
        let rows = legacy_private_settings(backup.path(), &[profile, unsafe_profile]).unwrap();
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].profile, profile);
        let session = rows[0].session.as_ref().unwrap();
        assert_eq!(session["base"], "https://example.com/base/");
        assert_eq!(session["automatic"], false);
    }
}
