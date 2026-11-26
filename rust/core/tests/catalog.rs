use serde_json::json;
use umd_core::{CatalogDocument, CatalogQuery, CatalogStore, ListEdit};
use uuid::Uuid;

#[test]
fn catalog_and_public_search_separate_same_name_comics_manga_and_novels() {
    let store = CatalogStore::in_memory().unwrap();
    let items: Vec<_> = ["book.manga", "book.novel", "book.light_novel", "book.comic"]
        .into_iter()
        .map(|subtype| {
            serde_json::from_value(json!({"name":"Homónimos","mediaType":"book","subtype":subtype}))
                .unwrap()
        })
        .collect();
    store.import(&CatalogDocument::new(items), false).unwrap();
    for (group, expected) in [("manga", 1), ("prose", 2), ("comic", 1)] {
        let query = CatalogQuery {
            q: "Homonimos".into(),
            filters: umd_core::CatalogFilters {
                subtype_group: Some(group.into()),
                ..Default::default()
            },
            ..Default::default()
        };
        let local = store.page(&query).unwrap().page.document.items;
        assert_eq!(local.len(), expected);
        assert!(local.iter().all(|item| item.matches_subtype_group(group)));
        let remote = store.encode_public_page(&query, true, 8 << 20, false).unwrap();
        assert_eq!(
            CatalogDocument::parse(&remote.body).unwrap().items.len(),
            expected
        );
    }
}

#[test]
fn public_page_stops_at_encoded_budget_and_resumes_without_repeating_rows() {
    let store = CatalogStore::in_memory().unwrap();
    let items: Vec<_> = (1..=3)
        .map(|n| {
            json!({
                "syncId": Uuid::from_u128(n), "name": format!("Título {n}"), "mediaType":"movie",
                "subtype":"movie", "description": "é".repeat(480),
            })
        })
        .collect();
    let catalog = CatalogDocument::parse(
        json!({"format":umd_core::CATALOG_FORMAT,"version":1,"items":items})
            .to_string()
            .as_bytes(),
    )
    .unwrap();
    store.import(&catalog, true).unwrap();
    for search in [false, true] {
        let mut query = CatalogQuery::default();
        for n in 1..=3 {
            let encoded = store.encode_public_page(&query, search, 1600, false).unwrap();
            assert!(encoded.body.len() <= 1600);
            let body: serde_json::Value = serde_json::from_slice(&encoded.body).unwrap();
            let items = if search {
                &body["items"]
            } else {
                &body["document"]["items"]
            };
            assert_eq!(items.as_array().unwrap().len(), 1);
            assert_eq!(items[0]["syncId"], json!(Uuid::from_u128(n)));
            if !search {
                assert_eq!(body["continuationToken"], json!(encoded.continuation_token));
            }
            query.cursor = encoded.continuation_token;
            assert_eq!(query.cursor.is_some(), n < 3);
        }
    }
}

#[test]
fn concurrent_publications_return_metadata_for_their_own_document() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("server.db");
    let writers: Vec<_> = (1..=6)
        .map(|n| (CatalogStore::open(&path).unwrap(), n))
        .collect();
    let gate = std::sync::Barrier::new(writers.len());
    std::thread::scope(|scope| {
        let mut jobs = Vec::new();
        for (db, n) in writers {
            let gate = &gate;
            jobs.push(scope.spawn(move || {
                let mut batch = document("First");
                for i in 1..n {
                    batch.items.extend(document(&format!("Title {i}")).items);
                }
                gate.wait();
                let metadata = db.publish(&batch).unwrap();
                assert_eq!(metadata.item_count, n);
                let mut expected = batch;
                for item in &mut expected.items {
                    item.sync_id = Some(item.id().unwrap());
                }
                expected.items.sort_by_key(|item| item.sync_id);
                expected.exported_at = metadata.updated_at.clone();
                use sha2::{Digest, Sha256};
                assert_eq!(
                    metadata.sha256,
                    format!(
                        "{:X}",
                        Sha256::digest(serde_json::to_vec(&expected).unwrap())
                    )
                );
                metadata.revision
            }));
        }
        let mut revisions: Vec<_> = jobs.into_iter().map(|job| job.join().unwrap()).collect();
        revisions.sort();
        assert_eq!(revisions, vec![1, 2, 3, 4, 5, 6]);
    });
}

fn document(name: &str) -> CatalogDocument {
    CatalogDocument::parse(serde_json::to_vec(&json!({
        "format": "universal-media-distribution.catalog",
        "version": 1,
        "items": [{
            "mediaType": "book", "subtype": "manga", "name": name,
            "genres": ["Aventura"],
            "units": [{"kind": "chapter", "displayNumber": "1", "sources": [{"torrent": {"magnetUri": "magnet:?xt=urn:btih:example"}}]}]
        }]
    })).unwrap().as_slice()).unwrap()
}

#[test]
fn exchanges_existing_dotnet_catalog_without_losing_nested_fields() {
    let original =
        CatalogDocument::parse(include_bytes!("../../../docs/examples/torrent-info.json")).unwrap();
    let store = CatalogStore::in_memory().unwrap();
    store.import(&original, false).unwrap();
    let exported = store.export().unwrap();
    assert_eq!(exported.items[0].metadata, original.items[0].metadata);
    assert_eq!(
        exported.items[0].sync_id,
        Some(original.items[0].id().unwrap())
    );
    let metadata = store.metadata().unwrap();
    use sha2::{Digest, Sha256};
    assert_eq!(
        metadata.sha256,
        format!(
            "{:X}",
            Sha256::digest(serde_json::to_vec(&exported).unwrap())
        )
    );
}

#[test]
fn reimport_and_rename_keep_favorites_and_explicit_identity() {
    let store = CatalogStore::in_memory().unwrap();
    let mut original = document("Cielos abiertos");
    original.items[0].sync_id = Some(Uuid::new_v5(&Uuid::NAMESPACE_URL, b"existing-dotnet-id"));
    let id = original.items[0].id().unwrap();
    store.import(&original, false).unwrap();
    store.set_favorite(id, true).unwrap();
    let mut legacy = original.clone();
    legacy.items[0].sync_id = None;
    store.import(&legacy, false).unwrap();
    original.items[0].name = "Nombre actualizado".into();
    store.import(&original, false).unwrap();
    let result = store
        .page(&CatalogQuery {
            favorites_only: true,
            ..Default::default()
        })
        .unwrap();
    assert_eq!(result.total, 1);
    assert_eq!(result.favorite_ids, vec![id]);
    assert_eq!(result.page.document.items[0].name, "Nombre actualizado");
}

#[test]
fn favorite_search_intersects_list_members_before_counting_or_paginating() {
    let store = CatalogStore::in_memory().unwrap();
    let ids: Vec<_> = (1..=4).map(Uuid::from_u128).collect();
    let mut items = Vec::new();
    for (index, &id) in ids.iter().enumerate() {
        let mut item = document(&format!("Libro {}", index + 1)).items.remove(0);
        item.sync_id = Some(id);
        item.metadata.insert(
            "alternativeNames".into(),
            json!([{"name":"Océano Azul","language":"es"}]),
        );
        items.push(item);
    }
    store.import(&CatalogDocument::new(items), false).unwrap();
    for &id in &ids[..3] {
        store.set_favorite(id, true).unwrap();
    }
    store
        .edit_list(ListEdit::Create {
            name: "Para leer".into(),
        })
        .unwrap();
    let lists = store.personal_lists().unwrap();
    let list_id = Uuid::parse_str(lists[0]["id"].as_str().unwrap()).unwrap();
    for &item_id in &ids[1..] {
        store
            .edit_list(ListEdit::Member {
                id: list_id,
                item_id,
                enabled: true,
            })
            .unwrap();
    }

    let mut query = CatalogQuery {
        q: "oceano azul".into(),
        favorites_only: true,
        limit: Some(1),
        ..Default::default()
    };
    query.filters.list_id = Some(list_id);
    let first = store.page(&query).unwrap();
    assert_eq!(first.total, 2);
    assert_eq!(first.page.document.items[0].sync_id, Some(ids[1]));
    query.cursor = first.page.continuation_token;
    assert!(query.cursor.is_some());
    let second = store.page(&query).unwrap();
    assert_eq!(second.total, 2);
    assert_eq!(second.page.document.items[0].sync_id, Some(ids[2]));
    assert!(second.page.continuation_token.is_none());
}

#[test]
fn ambiguous_legacy_import_rolls_back_after_preserving_distinct_remakes() {
    let store = CatalogStore::in_memory().unwrap();
    let original = document("Existing");
    store.import(&original, false).unwrap();
    let mut conflict = original.items[0].clone();
    conflict.sync_id = Some(Uuid::new_v5(&Uuid::NAMESPACE_URL, b"conflicting-id"));
    let mut incoming = document("New");
    store
        .import(&CatalogDocument::new(vec![conflict]), false)
        .unwrap();
    let before = store.metadata().unwrap();
    incoming.items.push(original.items[0].clone());
    assert!(store.import(&incoming, false).is_err());
    let after = store.metadata().unwrap();
    assert_eq!(before.revision, after.revision);
    assert_eq!(before.sha256, after.sha256);
    assert_eq!(after.item_count, 2);
}

#[test]
fn upgrading_title_index_keeps_old_favorites_and_allows_remakes() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("library.db");
    let db = rusqlite::Connection::open(&path).unwrap();
    db.execute_batch("CREATE TABLE rust_catalog(id TEXT PRIMARY KEY, name_key TEXT NOT NULL UNIQUE,media_type TEXT NOT NULL,search_text TEXT NOT NULL,payload TEXT NOT NULL);
        CREATE TABLE rust_favorites(id TEXT PRIMARY KEY REFERENCES rust_catalog(id) ON DELETE CASCADE);").unwrap();
    let mut film = document("Twin").items.remove(0);
    let id = film.id().unwrap();
    film.sync_id = Some(id);
    db.execute(
        "INSERT INTO rust_catalog VALUES(?1,?2,'book','twin',?3)",
        rusqlite::params![
            id.to_string(),
            film.key(),
            serde_json::to_string(&film).unwrap()
        ],
    )
    .unwrap();
    db.execute("INSERT INTO rust_favorites VALUES(?1)", [id.to_string()])
        .unwrap();
    drop(db);
    let store = CatalogStore::open(path).unwrap();
    film.sync_id = Some(Uuid::new_v4());
    store
        .import(&CatalogDocument::new(vec![film]), false)
        .unwrap();
    let page = store.page(&Default::default()).unwrap();
    assert_eq!(page.total, 2);
    assert_eq!(page.favorite_ids, vec![id]);
    store.export().unwrap().validate().unwrap();
}

#[test]
fn paging_uses_stable_cursors_without_skipping_or_duplicating_items() {
    let store = CatalogStore::in_memory().unwrap();
    let mut catalog = document("Árbol 1");
    for index in 2..=7 {
        catalog
            .items
            .push(document(&format!("Arbol {index}")).items.remove(0));
    }
    store.import(&catalog, false).unwrap();
    let mut query = CatalogQuery {
        q: "arbol".into(),
        limit: Some(2),
        ..Default::default()
    };
    let mut ids = Vec::new();
    loop {
        let result = store.page(&query).unwrap();
        assert_eq!(result.total, 7);
        ids.extend(
            result
                .page
                .document
                .items
                .iter()
                .map(|item| item.id().unwrap()),
        );
        query.cursor = result.page.continuation_token;
        if query.cursor.is_none() {
            break;
        }
    }
    let count = ids.len();
    ids.sort();
    ids.dedup();
    assert_eq!(count, 7);
    assert_eq!(ids.len(), 7);
}

#[test]
fn persists_imports_and_favorites_across_restart() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("catalog.db");
    let catalog = document("Persisted");
    let id = catalog.items[0].id().unwrap();
    {
        let store = CatalogStore::open(&path).unwrap();
        store.import(&catalog, false).unwrap();
        store.set_favorite(id, true).unwrap();
    }
    let store = CatalogStore::open(path).unwrap();
    assert_eq!(
        store.page(&CatalogQuery::default()).unwrap().favorite_ids,
        vec![id]
    );
}

#[test]
fn a_catalog_reader_sees_committed_data_during_a_background_write() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("catalog.db");
    let library = CatalogStore::open(&path).unwrap();
    let titles = document("Título visible");
    let title = titles.items[0].id().unwrap();
    library.import(&titles, false).unwrap();
    let profile_id = library.profiles().unwrap().active_id;
    let reader = CatalogStore::open_read_only(&path)
        .unwrap()
        .for_profile(profile_id.parse().unwrap())
        .unwrap();

    let mut writer = rusqlite::Connection::open(&path).unwrap();
    let pending = writer
        .transaction_with_behavior(rusqlite::TransactionBehavior::Immediate)
        .unwrap();
    pending
        .execute(
            "INSERT INTO rust_favorites(profile_id,id) VALUES(?1,?2)",
            rusqlite::params![profile_id, title.to_string()],
        )
        .unwrap();
    let query = CatalogQuery {
        favorites_only: true,
        ..Default::default()
    };
    assert_eq!(reader.page(&query).unwrap().total, 0);
    pending.commit().unwrap();
    assert_eq!(reader.page(&query).unwrap().favorite_ids, vec![title]);
}

#[test]
fn rejects_unsupported_versions_and_invalid_items_before_mutating_storage() {
    let store = CatalogStore::in_memory().unwrap();
    let mut catalog = document("Valid");
    catalog.version = 2;
    assert!(store.import(&catalog, true).is_err());
    catalog.version = 1;
    catalog.items[0].subtype = "not-a-subtype".into();
    assert!(store.import(&catalog, true).is_err());
    assert_eq!(store.metadata().unwrap().revision, 0);
}

#[test]
fn legacy_id_generation_matches_dotnet_guid_layout_and_book_qualification() {
    let mut item = document(" Cielos  Ábiertos ").items.remove(0);
    item.media_type = "Movie".into();
    item.subtype = "anime series".into();
    item.validate().unwrap();
    assert_eq!(
        item.id().unwrap().to_string(),
        "7645b45f-26ea-6bb9-abb2-ccb7a63bec43"
    );
    item.name = "Open Skies".into();
    item.media_type = "Book".into();
    item.subtype = "manga".into();
    assert_eq!(
        item.id().unwrap().to_string(),
        "9790750c-bc7b-2f09-ba51-46bfb0d14aa1"
    );
    item.subtype = "novel".into();
    assert_eq!(
        item.id().unwrap().to_string(),
        "e0634d15-3227-5644-4e99-9db7584f7366"
    );
    item.sync_id = Some(Uuid::parse_str("9790750c-bc7b-2f09-ba51-46bfb0d14aa1").unwrap());
    assert_eq!(
        item.id().unwrap().to_string(),
        "e0634d15-3227-5644-4e99-9db7584f7366"
    );
}

#[test]
fn display_filters_apply_before_count_sort_and_cursor() {
    use umd_core::{CatalogDocument, CatalogQuery, CatalogStore};
    let store = CatalogStore::in_memory().unwrap();
    let items=["A","B","C","D"].iter().enumerate().map(|(index,name)|serde_json::from_value(serde_json::json!({
        "syncId":uuid::Uuid::from_u128(index as u128+1),"name":name,"mediaType":"movie","subtype":"movie",
        "onlineProviders":if index==2{serde_json::json!({"favorite":"123"})}else{serde_json::json!({})},
        "categories":if index==1{vec![" +18 "]}else{vec!["Drama"]},"genres":["Drama"]
    })).unwrap()).collect();
    store.import(&CatalogDocument::new(items), false).unwrap();
    let mut query = CatalogQuery {
        limit: Some(1),
        ..Default::default()
    };
    query.filters.genre = Some("Drama".into());
    let mut names = Vec::new();
    loop {
        let page = store
            .display_page(&query, true, &["favorite".into()])
            .unwrap();
        assert_eq!(page.total, 3);
        names.push(page.page.document.items[0].name.clone());
        query.cursor = page.page.continuation_token;
        if query.cursor.is_none() {
            break;
        }
    }
    assert_eq!(names, ["C", "A", "D"]);
}

#[test]
fn search_text_excludes_json_keys_and_syntax() {
    let store = CatalogStore::in_memory().unwrap();
    let item = serde_json::from_value(json!({
        "mediaType": "movie",
        "subtype": "series",
        "name": "Nebula",
        "description": "Viaje espacial",
        "aliases": [{"name": "Estrella", "aliasType": "translation"}],
        "contributors": [{
            "name": "Estudio Sur",
            "kind": "organization",
            "credits": [{"role": "director", "characterName": "Capitán"}]
        }]
    }))
    .unwrap();
    store
        .import(&CatalogDocument::new(vec![item]), false)
        .unwrap();
    for syntax_or_key in ["aliasType", "characterName", "credits", "{", "[", "\":"] {
        let res = store
            .page(&CatalogQuery {
                q: syntax_or_key.into(),
                ..Default::default()
            })
            .unwrap();
        assert_eq!(
            res.total, 0,
            "should not match JSON key/syntax {syntax_or_key}"
        );
    }
    for leaf in ["estrella", "estudio sur", "capitan", "espacial"] {
        let res = store
            .page(&CatalogQuery {
                q: leaf.into(),
                ..Default::default()
            })
            .unwrap();
        assert_eq!(res.total, 1, "should match leaf text {leaf}");
    }
}

#[test]
fn subtype_group_status_provider_and_genre_filters_work_accurately() {
    let store = CatalogStore::in_memory().unwrap();
    let items = vec![
        json!({
            "syncId": Uuid::from_u128(10),
            "name": "Anime Movie",
            "mediaType": "movie",
            "subtype": "anime_movie",
            "publicationStatus": "completed",
            "contributors": [{"name": "Editorial Norte", "kind": "organization", "credits": [{"role": "editorial"}]}]
        }),
        json!({
            "syncId": Uuid::from_u128(11),
            "name": "Live Movie",
            "mediaType": "movie",
            "subtype": "movie",
            "contributors": [{"name": "Persona X", "kind": "person", "credits": [{"role": "distributor"}]}]
        }),
        json!({
            "syncId": Uuid::from_u128(12),
            "name": "Comic Run",
            "mediaType": "book",
            "subtype": "comic",
            "publicationStatus": "ongoing",
            "onlineProviders": {"capibaratraductor": "55"}
        }),
        json!({
            "syncId": Uuid::from_u128(13),
            "name": "Light Novel",
            "mediaType": "book",
            "subtype": "light_novel",
            "publicationStatus": "completed"
        }),
        json!({
            "syncId": Uuid::from_u128(14),
            "name": "Web Novel",
            "mediaType": "book",
            "subtype": "web_novel",
            "publicationStatus": "hiatus"
        }),
        json!({
            "syncId": Uuid::from_u128(15),
            "name": "Podcast Show",
            "mediaType": "audio",
            "subtype": "podcast",
            "genres": null,
            "categories": null
        }),
    ]
    .into_iter()
    .map(|v| serde_json::from_value(v).unwrap())
    .collect();
    store.import(&CatalogDocument::new(items), false).unwrap();

    // subtype_group=anime must NOT leak books or audio.
    let mut q = CatalogQuery::default();
    q.filters.subtype_group = Some("anime".into());
    let anime = store.page(&q).unwrap();
    assert_eq!(anime.total, 1);
    assert_eq!(anime.page.document.items[0].name, "Anime Movie");

    q.filters.subtype_group = Some("liveaction".into());
    let live = store.page(&q).unwrap();
    assert_eq!(live.total, 1);
    assert_eq!(live.page.document.items[0].name, "Live Movie");

    // status includes comic, light_novel, web_novel.
    for (status, expected) in [("ongoing", "Comic Run"), ("paused", "Web Novel")] {
        let mut sq = CatalogQuery::default();
        sq.filters.status = Some(status.into());
        let res = store.page(&sq).unwrap();
        assert_eq!(res.total, 1);
        assert_eq!(res.page.document.items[0].name, expected);
    }
    let mut sq = CatalogQuery::default();
    sq.filters.status = Some("completed".into());
    assert_eq!(store.page(&sq).unwrap().total, 2);

    // provider matches organization distributor roles & onlineProviders keys, rejects person kind.
    let mut pq = CatalogQuery::default();
    pq.filters.provider = Some("  editorial norte ".into());
    assert_eq!(store.page(&pq).unwrap().total, 1);
    pq.filters.provider = Some("Persona X".into());
    assert_eq!(store.page(&pq).unwrap().total, 0);
    pq.filters.provider = Some("capibaratraductor".into());
    assert_eq!(store.page(&pq).unwrap().total, 1);

    // Blank category/provider ignored safely even with null arrays.
    let mut bq = CatalogQuery::default();
    bq.filters.category = Some("   ".into());
    bq.filters.provider = Some("  ".into());
    assert_eq!(store.page(&bq).unwrap().total, 6);

    // Genre validation rejects leading/trailing/double hyphens, accepts case-normalized keys.
    for bad in ["-action", "action-", "sci--fi", "a"] {
        let mut gq = CatalogQuery::default();
        gq.filters.genre = Some(bad.into());
        assert!(store.page(&gq).is_err(), "should reject genre {bad}");
    }
    let mut gq = CatalogQuery::default();
    gq.filters.genre = Some("Sci-Fi".into());
    assert!(store.page(&gq).is_ok());
}

#[test]
fn display_page_hides_adult_favorites_and_deleted_preferred_cursor_does_not_rewind() {
    let store = CatalogStore::in_memory().unwrap();
    let pref_id = Uuid::from_u128(1);
    let adult_id = Uuid::from_u128(2);
    let norm_a = Uuid::from_u128(3);
    let norm_b = Uuid::from_u128(4);
    let items = vec![
        json!({"syncId": pref_id, "name": "Pref", "mediaType": "movie", "subtype": "movie", "onlineProviders": {"fav": "1"}}),
        json!({"syncId": adult_id, "name": "Adulto", "mediaType": "movie", "subtype": "movie", "categories": ["Erótico"], "contentRating": "NC-17"}),
        json!({"syncId": norm_a, "name": "Normal A", "mediaType": "movie", "subtype": "movie"}),
        json!({"syncId": norm_b, "name": "Normal B", "mediaType": "movie", "subtype": "movie"}),
    ]
    .into_iter()
    .map(|v| serde_json::from_value(v).unwrap())
    .collect();
    store.import(&CatalogDocument::new(items), false).unwrap();
    store.set_favorite(pref_id, true).unwrap();
    store.set_favorite(adult_id, true).unwrap();

    let hidden = store
        .display_page(&CatalogQuery::default(), true, &["fav".into()])
        .unwrap();
    assert_eq!(hidden.total, 3);
    assert_eq!(hidden.favorite_ids, vec![pref_id]);

    let visible = store
        .display_page(&CatalogQuery::default(), false, &["fav".into()])
        .unwrap();
    assert_eq!(visible.favorite_ids.len(), 2);

    // Page with limit=1: page 1 -> Pref, page 2 -> Normal A; delete Normal A, page 3 -> Normal B (NO rewind to Pref!).
    let mut q = CatalogQuery {
        limit: Some(1),
        ..Default::default()
    };
    let p1 = store.display_page(&q, true, &["fav".into()]).unwrap();
    assert_eq!(p1.page.document.items[0].sync_id, Some(pref_id));
    q.cursor = p1.page.continuation_token;
    let p2 = store.display_page(&q, true, &["fav".into()]).unwrap();
    assert_eq!(p2.page.document.items[0].sync_id, Some(norm_a));
    q.cursor = p2.page.continuation_token;

    // Delete cursor item norm_a via tombstone import.
    let tombstone = serde_json::from_value(json!({
        "syncId": norm_a, "name": "Normal A", "mediaType": "movie", "subtype": "movie", "deleted": true
    }))
    .unwrap();
    store
        .import(&CatalogDocument::new(vec![tombstone]), false)
        .unwrap();

    let p3 = store.display_page(&q, true, &["fav".into()]).unwrap();
    assert_eq!(p3.page.document.items.len(), 1);
    assert_eq!(p3.page.document.items[0].sync_id, Some(norm_b));
}

#[test]
fn saved_editions_emits_capibara_alongside_other_providers_and_metadata_cache_tracks_revisions() {
    let store = CatalogStore::in_memory().unwrap();
    let cap_id = Uuid::from_u128(umd_core::capibara::EDITION_PREFIX | 77);
    let cap_dup_id = Uuid::from_u128(umd_core::capibara::EDITION_PREFIX | 88);
    let items = vec![
        serde_json::from_value(json!({
            "syncId": cap_id,
            "name": "Dual",
            "mediaType": "book",
            "subtype": "manga",
            "onlineProviders": {"leercapitulo": "slug-1"}
        }))
        .unwrap(),
        serde_json::from_value(json!({
            "syncId": cap_dup_id,
            "name": "AlreadyCap",
            "mediaType": "book",
            "subtype": "manga",
            "onlineProviders": {"capibaratraductor": "88"}
        }))
        .unwrap(),
    ];
    store.import(&CatalogDocument::new(items), false).unwrap();
    let editions = store.saved_editions().unwrap();
    assert!(editions
        .iter()
        .any(|(id, _, site, ext)| *id == cap_id && site == "leercapitulo" && ext == "slug-1"));
    assert!(editions
        .iter()
        .any(|(id, _, site, ext)| *id == cap_id && site == "capibaratraductor" && ext == "77"));
    assert_eq!(
        editions
            .iter()
            .filter(|(id, _, site, _)| *id == cap_dup_id && site == "capibaratraductor")
            .count(),
        1
    );

    let m1 = store.metadata().unwrap();
    let m2 = store.metadata().unwrap();
    assert_eq!(m1.revision, m2.revision);
    assert_eq!(m1.sha256, m2.sha256);
    store.import(&document("Extra"), false).unwrap();
    let m3 = store.metadata().unwrap();
    assert!(m3.revision > m1.revision);
    assert_ne!(m3.sha256, m1.sha256);
    assert_eq!(m3.item_count, 3);
}

#[test]
fn recent_shelf_keeps_arrival_order_across_full_publications() {
    let store = CatalogStore::in_memory().unwrap();
    let mut catalog = document("First");
    catalog.items.extend(document("Second").items);
    store.publish(&catalog).unwrap();
    let mut next = document("Third");
    next.items.extend(catalog.items.into_iter().rev());
    store.publish(&next).unwrap();
    let names: Vec<_> = store
        .recently_added(false)
        .unwrap()
        .items
        .into_iter()
        .map(|item| item.name)
        .collect();
    assert_eq!(names, ["Third", "Second", "First"]);
}
