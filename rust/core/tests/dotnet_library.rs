use rusqlite::Connection;
use std::fs;
use umd_core::{prepare_library_restore, CatalogStore};

#[test]
fn oldest_dotnet_library_restores_without_rewriting_the_source() {
    let dir = tempfile::tempdir().unwrap();
    let source = dir.path().join("old.umdbackup");
    let db = Connection::open(&source).unwrap();
    // Use the exact schema that shipped with the first client.
    db.execute_batch(include_str!("fixtures/dotnet-v1.sql"))
        .unwrap();
    db.execute_batch("INSERT INTO catalog_items(id,media_type,subtype_code,name,name_key,created_at,updated_at)
        VALUES('a','movie','movie.series','Serie','SERIE','2024-01-01','2024-01-01');
        INSERT INTO library_entries(catalog_item_id,is_favorite,added_at) VALUES('a',1,'2024-01-01 00:00:00');
        INSERT INTO seasons VALUES('s','a',2,'Segunda',NULL,NULL,0);
        INSERT INTO content_units(id,catalog_item_id,season_id,unit_kind,display_number,display_number_key,sort_order)
          VALUES('u','a','s','episode','3','3',1);
        INSERT INTO playback_progress VALUES('p',NULL,'u',12,'seconds',0,'2024-01-01 00:00:00');
        INSERT INTO torrent_packages(id,natural_key,torrent_metadata,created_at,updated_at) VALUES('t','k',X'0102','now','now');
        INSERT INTO media_sources(id,natural_key,torrent_package_id,content_unit_id,asset_role,source_layout)
          VALUES('m','k','t','u','video','single_file');").unwrap();
    drop(db);
    let original = fs::read(&source).unwrap();
    let pending = tempfile::NamedTempFile::new_in(&dir).unwrap();
    assert!(prepare_library_restore(&source, pending.path()).unwrap());
    assert_eq!(fs::read(&source).unwrap(), original);
    let live = dir.path().join("live.db");
    drop(CatalogStore::open(&live).unwrap());
    CatalogStore::restore_database_at_start(&live, pending.path(), &dir.path().join("previous.db"))
        .unwrap();
    let data = CatalogStore::open(&live).unwrap().backup().unwrap();
    let unit = &data["catalog"]["items"][0]["seasons"][0]["units"][0];
    assert_eq!(
        unit["sources"][0]["torrent"]["torrentMetadataBase64"],
        "AQI="
    );
    assert_eq!(data["progress"][0]["content"]["seasonNumber"], 2);
    assert_eq!(data["progress"][0]["position"], 12.0);
    assert_eq!(data["favoriteItemStates"][0]["isFavorite"], true);
    assert!(!prepare_library_restore(pending.path(), &source).unwrap());
    let db = Connection::open(&source).unwrap();
    db.execute_batch("PRAGMA user_version=99").unwrap();
    assert!(prepare_library_restore(&source, pending.path()).is_err());

    // Missing application_id returns Error::Invalid (not QueryReturnedNoRows).
    let no_app = dir.path().join("no_app.umdbackup");
    let db = Connection::open(&no_app).unwrap();
    db.execute_batch("CREATE TABLE schema_info(key TEXT PRIMARY KEY, value TEXT);")
        .unwrap();
    drop(db);
    let out = tempfile::NamedTempFile::new_in(&dir).unwrap();
    assert!(matches!(
        prepare_library_restore(&no_app, out.path()),
        Err(umd_core::Error::Invalid(_))
    ));
}

#[test]
fn dotnet_restore_preserves_credential_key_normalizes_timestamps_and_skips_orphaned_list_tombstones(
) {
    let dir = tempfile::tempdir().unwrap();
    let source = dir.path().join("v8.umdbackup");
    let db = Connection::open(&source).unwrap();
    db.execute_batch(include_str!("fixtures/dotnet-v1.sql"))
        .unwrap();
    // Upgrade minimal v1 schema to v8 with credential_key, lists, and user_sync_records.
    db.execute_batch(
        "ALTER TABLE tracker_endpoints ADD COLUMN credential_key TEXT NULL;
         ALTER TABLE torrent_packages ADD COLUMN language TEXT NOT NULL DEFAULT 'und';
         ALTER TABLE contributors ADD COLUMN url TEXT NULL;
         CREATE TABLE application_settings(key TEXT PRIMARY KEY, value TEXT);
         INSERT INTO application_settings VALUES('content_filter_mode','Complete');
         INSERT INTO application_settings VALUES('content_filter_hide_adult','True');
         CREATE TABLE catalog_alternative_names(id TEXT PRIMARY KEY, catalog_item_id TEXT NOT NULL, name TEXT NOT NULL, language TEXT, alias_type TEXT);
         CREATE TABLE categories(id TEXT PRIMARY KEY, name TEXT NOT NULL);
         CREATE TABLE catalog_item_categories(catalog_item_id TEXT NOT NULL, category_id TEXT NOT NULL);
         CREATE TABLE favorite_contributors(contributor_key TEXT NOT NULL PRIMARY KEY, added_at TEXT NOT NULL);
         CREATE TABLE user_media_lists(id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
         CREATE TABLE user_media_list_items(list_id TEXT NOT NULL, catalog_item_id TEXT NOT NULL, added_at TEXT NOT NULL);
         CREATE TABLE user_sync_records(record_kind TEXT NOT NULL, record_key TEXT NOT NULL, sync_clock INTEGER NOT NULL,
             sync_device_id TEXT NOT NULL, sync_operation_id TEXT NOT NULL, modified_at TEXT NOT NULL,
             is_deleted INTEGER NOT NULL, item_media_type TEXT, item_name TEXT);
         PRAGMA user_version = 8;",
    )
    .unwrap();
    let list_id = "ABCDEF01-2345-6789-ABCD-EF0123456789";
    let orphan_list_id = "11111111222233334444555566667777";
    let op1 = uuid::Uuid::new_v4().to_string();
    let op2 = uuid::Uuid::new_v4().to_string();
    db.execute_batch(&format!(
        "INSERT INTO catalog_items(id,media_type,subtype_code,name,name_key,created_at,updated_at)
            VALUES('a','movie','movie.movie','Pelicula','PELICULA','2024-01-01','2024-01-01');
         INSERT INTO tracker_endpoints(id,natural_key,name,endpoint_template,transport,client_family,requires_authentication,credential_key,is_enabled)
            VALUES('tr1','priv','Priv','https://tracker.example/ann','https','native',1,'cred-key-42',1);
         INSERT INTO torrent_packages(id,natural_key,magnet_uri,created_at,updated_at)
            VALUES('tp1','k','magnet:?xt=urn:btih:abc','2024-01-01','2024-01-01');
         INSERT INTO torrent_package_trackers(torrent_package_id,tracker_endpoint_id,tier,sort_order)
            VALUES('tp1','tr1',0,0);
         INSERT INTO media_sources(id,natural_key,torrent_package_id,catalog_item_id,asset_role,source_layout)
            VALUES('ms1','k','tp1','a','video','single_file');
         INSERT INTO playback_progress VALUES('p1','a',NULL,30,'seconds',0,'2024-01-02 10:00:00-03:00');
         INSERT INTO library_entries(catalog_item_id,is_favorite,added_at) VALUES('a',1,'   ');
         INSERT INTO user_media_lists VALUES('{list_id}','Mi Lista','2024-01-01T00:00:00Z','2024-01-01T00:00:00Z');
         INSERT INTO user_media_list_items VALUES('{list_id}','a','2024-01-01 12:00:00+00:00');
         INSERT INTO user_sync_records VALUES('list_item','{orphan_list_id}|movie:PELICULA',2,'dev','{op1}','2024-01-03T00:00:00Z',1,'movie','Pelicula');
         INSERT INTO user_sync_records VALUES('list_item','{list_id}|movie:PELICULA',1,'dev','{op2}','2024-01-01T12:00:00Z',0,'movie','Pelicula');"
    ))
    .unwrap();
    drop(db);

    let converted = tempfile::NamedTempFile::new_in(&dir).unwrap();
    assert!(prepare_library_restore(&source, converted.path()).unwrap());
    let store = CatalogStore::open(converted.path()).unwrap();
    let data = store.backup().unwrap();
    assert_eq!(
        store.setting("player_ad_filter").unwrap().as_deref(),
        Some("complete")
    );
    let active = store.profiles().unwrap().active_id;
    let imported: serde_json::Value = serde_json::from_str(
        &store
            .setting(&format!("legacy-prefs-{active}"))
            .unwrap()
            .unwrap(),
    )
    .unwrap();
    assert_eq!(imported["values"]["umd-hide-adult"], "true");
    umd_core::validate_backup(&data).unwrap();
    assert_eq!(
        data["catalog"]["items"][0]["sources"][0]["torrent"]["trackers"][0]["credentialKey"],
        "cred-key-42"
    );
    assert_eq!(
        data["progress"][0]["updatedAt"],
        "2024-01-02T10:00:00-03:00"
    );
    assert_eq!(data["lists"].as_array().unwrap().len(), 1);
    assert_eq!(data["lists"][0]["items"].as_array().unwrap().len(), 1);
}

#[test]
#[ignore = "Run with UMD_TEST_DOTNET_LIBRARY pointing at the two-profile .NET fixture"]
fn current_dotnet_backup_keeps_same_name_books_and_separate_profiles() {
    let source = std::env::var_os("UMD_TEST_DOTNET_LIBRARY").unwrap();
    let dest = tempfile::NamedTempFile::new().unwrap();
    assert!(prepare_library_restore(source.as_ref(), dest.path()).unwrap());
    let store = CatalogStore::open(dest.path()).unwrap();
    let profiles = store.profiles().unwrap();
    assert_eq!(profiles.profiles.len(), 2);
    for profile in profiles.profiles {
        let second = profile.name == "Segundo";
        assert_eq!(profiles.active_id == profile.id, second);
        let library = store.for_profile(profile.id.parse().unwrap()).unwrap();
        let data = library.backup().unwrap();
        umd_core::validate_backup(&data).unwrap();
        let item = &data["favoriteItemStates"][0]["item"];
        assert_eq!(data["catalog"]["items"].as_array().unwrap().len(), 2);
        for title in data["catalog"]["items"].as_array().unwrap() {
            assert_eq!(
                title["onlineProviders"]["leercapitulo"],
                format!("book.{}", title["subtype"].as_str().unwrap())
            );
        }
        assert_eq!(data["lists"][0]["items"][0], *item);
        assert_eq!(item["subtype"], if second { "novel" } else { "manga" });
        assert_eq!(data["progress"][0]["content"]["item"], *item);
        assert_eq!(
            data["progress"][0]["position"],
            if second { 19.0 } else { 7.0 }
        );
        assert_eq!(data["readerPreferences"][0]["displayMode"], "continuous");
    }
}
